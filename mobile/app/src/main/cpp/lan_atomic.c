#include <jni.h>
#include <errno.h>
#include <dirent.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#ifndef RENAME_NOREPLACE
#define RENAME_NOREPLACE 1
#endif

static void fail(JNIEnv *env,const char *code) {
    jclass cls=(*env)->FindClass(env,"java/io/IOException");
    if(cls) (*env)->ThrowNew(env,cls,code); /* Stable code only; never copy private paths/errno text. */
}
/* JNI modified UTF-8 is not a filesystem encoding (notably for emoji). */
static char *utf8(JNIEnv *env,jstring value) {
    jsize count=(*env)->GetStringLength(env,value);
    if(count>8192) { fail(env,"path_budget");return NULL; }
    const jchar *chars=(*env)->GetStringChars(env,value,NULL);if(!chars)return NULL;
    char *out=malloc((size_t)count*4+1);if(!out) { (*env)->ReleaseStringChars(env,value,chars);fail(env,"memory_budget");return NULL; }
    size_t n=0;int valid=1;
    for(jsize i=0;i<count;i++) {
        uint32_t c=chars[i];
        if(c>=0xd800 && c<=0xdbff) { if(i+1>=count || chars[i+1]<0xdc00 || chars[i+1]>0xdfff) { valid=0;break; }c=0x10000+((c-0xd800)<<10)+(chars[++i]-0xdc00); }
        else if(c>=0xdc00 && c<=0xdfff) { valid=0;break; }
        if(!c) { valid=0;break; }
        if(c<0x80)out[n++]=(char)c;
        else if(c<0x800) { out[n++]=(char)(0xc0|(c>>6));out[n++]=(char)(0x80|(c&63)); }
        else if(c<0x10000) { out[n++]=(char)(0xe0|(c>>12));out[n++]=(char)(0x80|((c>>6)&63));out[n++]=(char)(0x80|(c&63)); }
        else { out[n++]=(char)(0xf0|(c>>18));out[n++]=(char)(0x80|((c>>12)&63));out[n++]=(char)(0x80|((c>>6)&63));out[n++]=(char)(0x80|(c&63)); }
    }
    (*env)->ReleaseStringChars(env,value,chars);
    if(!valid) { free(out);fail(env,"invalid_path_encoding");return NULL; }out[n]=0;return out;
}
static int component(const char *s) { return *s && strcmp(s,".") && strcmp(s,"..") && !strchr(s,'/') && !strchr(s,'\\'); }
static int walk(int initial,const char *path,int create) {
    int current=dup(initial);if(current<0)return -1;
    char *copy=strdup(path);if(!copy) { close(current);errno=ENOMEM;return -1; }
    char *save=NULL;
    for(char *part=strtok_r(copy,"/",&save);part;part=strtok_r(NULL,"/",&save)) {
        if(!component(part)) { close(current);free(copy);errno=EINVAL;return -1; }
        int next=openat(current,part,O_PATH|O_DIRECTORY|O_CLOEXEC|O_NOFOLLOW);
        if(next<0 && errno==ENOENT && create) {
            if(mkdirat(current,part,0700)==0 || errno==EEXIST) next=openat(current,part,O_PATH|O_DIRECTORY|O_CLOEXEC|O_NOFOLLOW);
        }
        if(next<0) { close(current);free(copy);return -1; }
        close(current);current=next;
    }
    free(copy);int readable=openat(current,".",O_RDONLY|O_DIRECTORY|O_CLOEXEC);close(current);return readable;
}
JNIEXPORT jint JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_openRoot(JNIEnv *env,jobject self,jstring value) {
    (void)self;const char *path=utf8(env,value);if(!path)return -1;
    int base=open("/",O_PATH|O_DIRECTORY|O_CLOEXEC);int result=-1;
    if(base>=0 && path[0]=='/')result=walk(base,path,0);
    if(base>=0)close(base);free((void*)path);
    if(result<0)fail(env,"root_unavailable_or_linked");return result;
}
JNIEXPORT jint JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_openParent(JNIEnv *env,jobject self,jint root,jstring value,jboolean create) {
    (void)self;const char *path=utf8(env,value);if(!path)return -1;
    int result=walk(root,path,create);int error=errno;free((void*)path);
    if(result<0 && error!=ENOENT)fail(env,"parent_unavailable_or_linked");return result;
}
JNIEXPORT jint JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_openLeaf(JNIEnv *env,jobject self,jint parent,jstring value,jboolean writable,jboolean create) {
    (void)self;const char *name=utf8(env,value);if(!name)return -1;
    int fd=-1;errno=EINVAL;if(component(name))fd=openat(parent,name,(writable?O_RDWR:O_RDONLY)|O_CLOEXEC|O_NOFOLLOW|(create?(O_CREAT|O_EXCL):0),0600);
    int error=errno;free((void*)name);
    if(fd<0) { if(!create && error==ENOENT)return -1;fail(env,error==EEXIST?"target_appeared":"file_unavailable_or_linked");return -1; }
    struct stat st;if(fstat(fd,&st)<0 || !S_ISREG(st.st_mode)) { close(fd);fail(env,"not_regular_file");return -1; }
    if(writable && flock(fd,LOCK_EX|LOCK_NB)<0) {
        int error=errno;
        /* Android shared-storage FUSE may not implement advisory flock. Content/identity
         * rechecks, no-replace renames and retained originals remain mandatory there. */
        if(error!=ENOSYS && error!=EOPNOTSUPP && error!=EINVAL) { close(fd);fail(env,error==EAGAIN || error==EWOULDBLOCK?"file_busy":"file_lock_unavailable");return -1; }
    }
    return fd;
}
static jlongArray identity(JNIEnv *env,struct stat *st) {
    jlong values[5]={(jlong)st->st_dev,(jlong)st->st_ino,(jlong)st->st_size,(jlong)st->st_mtim.tv_sec,(jlong)st->st_mtim.tv_nsec};
    jlongArray result=(*env)->NewLongArray(env,5);if(result)(*env)->SetLongArrayRegion(env,result,0,5,values);return result;
}
JNIEXPORT jlongArray JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_identity(JNIEnv *env,jobject self,jint fd) {
    (void)self;struct stat st;if(fstat(fd,&st)<0 || (!S_ISREG(st.st_mode) && !S_ISDIR(st.st_mode))) { fail(env,"file_identity_unavailable");return NULL; }return identity(env,&st);
}
JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_setModified(JNIEnv *env,jobject self,jint fd,jlong ns) {
    (void)self;if(ns<=0)return;
    struct timespec times[2]={{.tv_nsec=UTIME_OMIT},{.tv_sec=ns/1000000000LL,.tv_nsec=ns%1000000000LL}};
    if(futimens(fd,times)<0)fail(env,"timestamp_unavailable");
}
JNIEXPORT jobjectArray JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_listPage(JNIEnv *env,jobject self,jint fd,jlong cookie) {
    (void)self;DIR *dir=fdopendir(dup(fd));if(!dir) { fail(env,"directory_state_unknown");return NULL; }
    seekdir(dir,(long)cookie);
    jbyteArray values[200];int count=0;struct dirent *entry;int read_error=0;
    while(count<200) {
        errno=0;entry=readdir(dir);if(!entry) { read_error=errno;break; }
        if(!strcmp(entry->d_name,".") || !strcmp(entry->d_name,".."))continue;
        struct stat st;char type='U';
        if(fstatat(fd,entry->d_name,&st,AT_SYMLINK_NOFOLLOW)==0)type=S_ISDIR(st.st_mode)?'D':S_ISREG(st.st_mode)?'F':'U';
        size_t length=strlen(entry->d_name);jbyteArray bytes=(*env)->NewByteArray(env,(jsize)length+1);
        if(!bytes) { closedir(dir);return NULL; }
        (*env)->SetByteArrayRegion(env,bytes,0,1,(const jbyte*)&type);(*env)->SetByteArrayRegion(env,bytes,1,(jsize)length,(const jbyte*)entry->d_name);values[count++]=bytes;
    }
    long next=telldir(dir);closedir(dir);
    if(read_error || next<0) { fail(env,"directory_state_unknown");return NULL; }
    jclass byte_array=(*env)->FindClass(env,"[B");jobjectArray result=(*env)->NewObjectArray(env,count+1,byte_array,NULL);if(!result)return NULL;
    char cursor[64];int length=snprintf(cursor,sizeof(cursor),"%ld",next);jbyteArray first=(*env)->NewByteArray(env,length);if(!first)return NULL;
    (*env)->SetByteArrayRegion(env,first,0,length,(const jbyte*)cursor);(*env)->SetObjectArrayElement(env,result,0,first);(*env)->DeleteLocalRef(env,first);
    for(int i=0;i<count;i++) { (*env)->SetObjectArrayElement(env,result,i+1,values[i]);(*env)->DeleteLocalRef(env,values[i]); }
    return result;
}
JNIEXPORT jlongArray JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_statLeaf(JNIEnv *env,jobject self,jint parent,jstring value) {
    (void)self;const char *name=utf8(env,value);if(!name)return NULL;
    struct stat st;errno=EINVAL;int result=component(name)?fstatat(parent,name,&st,AT_SYMLINK_NOFOLLOW):-1;int error=errno;free((void*)name);
    if(result<0) { if(error!=ENOENT)fail(env,"file_state_unknown");return NULL; }
    if(!S_ISREG(st.st_mode)) { fail(env,"not_regular_file");return NULL; }return identity(env,&st);
}
JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_moveNoReplace(JNIEnv *env,jobject self,jint parent,jstring source,jstring destination,jlong device,jlong inode) {
    (void)self;const char *src=utf8(env,source);if(!src)return;
    const char *dst=utf8(env,destination);if(!dst) { free((void*)src);return; }
    struct stat st;int valid=component(src)&&component(dst)&&fstatat(parent,src,&st,AT_SYMLINK_NOFOLLOW)==0&&S_ISREG(st.st_mode)&&st.st_dev==(dev_t)device&&st.st_ino==(ino_t)inode;
    if(!valid)fail(env,"source_identity_changed");
    else if(syscall(SYS_renameat2,parent,src,parent,dst,RENAME_NOREPLACE)<0)fail(env,errno==EEXIST?"target_appeared":errno==ENOSYS || errno==EOPNOTSUPP || errno==EINVAL?"rename_noreplace_unsupported":"atomic_rename_unavailable");
    else {
        if(fstatat(parent,dst,&st,AT_SYMLINK_NOFOLLOW)<0 || st.st_dev!=(dev_t)device || st.st_ino!=(ino_t)inode) {
            /* Preserve all copies and attempt a no-overwrite rollback of a racing source name. */
            syscall(SYS_renameat2,parent,dst,parent,src,RENAME_NOREPLACE);fail(env,"source_identity_changed");
        } else if(fsync(parent)<0 && errno!=EINVAL)fail(env,"directory_sync_failed");
    }
    free((void*)src);free((void*)dst);
}
JNIEXPORT jint JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_openVault(JNIEnv *env,jobject self,jint parent,jstring value,jboolean create) {
    (void)self;const char *name=utf8(env,value);if(!name)return -1;
    int valid=component(name)&&!strncmp(name,".jeek-sync-",11)&&strstr(name,".recovery")!=NULL;
    if(!valid || (create && mkdirat(parent,name,0700)<0)) { free((void*)name);fail(env,"recovery_vault_unavailable");return -1; }
    int fd=openat(parent,name,O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);free((void*)name);
    /* Emulated storage synthesizes UID/mode. Ownership is the exclusive mkdir receipt
     * and the directory identity in private SQLite, not that synthesized UID. */
    struct stat st;if(fd<0 || fstat(fd,&st)<0 || !S_ISDIR(st.st_mode)) { if(fd>=0)close(fd);fail(env,"recovery_vault_identity");return -1; }
    return fd;
}
JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_moveIntoVault(JNIEnv *env,jobject self,jint parent,jstring value,jint vault,jlong device,jlong inode) {
    (void)self;const char *name=utf8(env,value);if(!name)return;
    struct stat src,dst;
    if(!component(name) || fstatat(parent,name,&src,AT_SYMLINK_NOFOLLOW)<0 || !S_ISREG(src.st_mode) || src.st_dev!=(dev_t)device || src.st_ino!=(ino_t)inode)fail(env,"source_identity_changed");
    else if(fstatat(vault,"original",&dst,AT_SYMLINK_NOFOLLOW)==0 || errno!=ENOENT)fail(env,"recovery_slot_occupied");
    /* Only a newly created, journal-pinned internal vault is a destination.
     * Public destinations never use ordinary overwrite rename. Remote paths cannot name vaults. */
    else if(renameat(parent,name,vault,"original")<0)fail(env,"recovery_move_unavailable");
    else {
        fsync(parent);fsync(vault);
        if(fstatat(vault,"original",&dst,AT_SYMLINK_NOFOLLOW)<0 || dst.st_dev!=(dev_t)device || dst.st_ino!=(ino_t)inode)fail(env,"source_identity_changed");
    }
    free((void*)name);
}
JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_removeVaultOriginal(JNIEnv *env,jobject self,jint vault,jlong device,jlong inode) {
    (void)self;struct stat st;
    if(fstatat(vault,"original",&st,AT_SYMLINK_NOFOLLOW)<0 || !S_ISREG(st.st_mode) || st.st_dev!=(dev_t)device || st.st_ino!=(ino_t)inode)fail(env,"backup_identity_changed");
    else if(unlinkat(vault,"original",0)<0)fail(env,"backup_cleanup_failed");
    else fsync(vault);
}
JNIEXPORT void JNICALL Java_com_tinyfish_jeekmusic_data_sync_LanAtomicFiles_removeOwned(JNIEnv *env,jobject self,jint parent,jstring value,jlong device,jlong inode) {
    (void)self;const char *name=utf8(env,value);if(!name)return;
    struct stat st;
    if(!component(name) || strncmp(name,".jeek-sync-",11) || fstatat(parent,name,&st,AT_SYMLINK_NOFOLLOW)<0 || !S_ISREG(st.st_mode) || st.st_dev!=(dev_t)device || st.st_ino!=(ino_t)inode)fail(env,"owned_cleanup_changed");
    else if(unlinkat(parent,name,0)<0)fail(env,"owned_cleanup_failed");
    free((void*)name);
}
