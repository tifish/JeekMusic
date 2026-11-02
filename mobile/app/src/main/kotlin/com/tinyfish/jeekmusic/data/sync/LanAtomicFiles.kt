package com.tinyfish.jeekmusic.data.sync

/** Descriptor-relative, no-symlink, no-overwrite primitives. Unsupported filesystems defer safely. */
internal object LanAtomicFiles {
    init { System.loadLibrary("jeek_sync") }
    external fun openRoot(path: String): Int
    external fun openParent(root: Int,path: String,create: Boolean): Int
    external fun openLeaf(parent: Int,name: String,writable: Boolean,create: Boolean): Int
    external fun identity(fd: Int): LongArray
    external fun setModified(fd: Int,nanoseconds: Long)
    external fun listPage(directory: Int,cookie: Long): Array<ByteArray>
    external fun statLeaf(parent: Int,name: String): LongArray?
    external fun moveNoReplace(parent: Int,source: String,target: String,device: Long,inode: Long)
    external fun removeOwned(parent: Int,name: String,device: Long,inode: Long)
    external fun openVault(parent: Int,name: String,create: Boolean): Int
    external fun moveIntoVault(parent: Int,name: String,vault: Int,device: Long,inode: Long)
    external fun removeVaultOriginal(vault: Int,device: Long,inode: Long)
}
