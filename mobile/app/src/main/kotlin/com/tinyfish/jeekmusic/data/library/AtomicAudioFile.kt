package com.tinyfish.jeekmusic.data.library

import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import java.io.File
import java.io.FileDescriptor
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.nio.file.Files
import java.nio.file.attribute.FileTime
import java.time.Instant
import java.security.MessageDigest
import com.tinyfish.jeekmusic.data.tags.SafeTagWriter

/** Local ExternalStorageProvider only: never implement SAF commit as truncate-and-copy. */
internal object AtomicAudioFile {
    fun replace(source: File, authorizedFd: FileDescriptor, transform: (File) -> Unit, validate: (File) -> Unit, canCommit: () -> Boolean): Boolean {
        val before = Os.fstat(authorizedFd)
        require(OsConstants.S_ISREG(before.st_mode) && before.st_nlink == 1L) { "Only ordinary, single-link local files can be replaced" }
        require(sameFile(before, Os.stat(source.path))) { "SAF descriptor/path mismatch" }
        val originalHash = SafeTagWriter.sha256(source)
        val temporary = File.createTempFile(".jeekmusic-tags-", ".tmp", source.parentFile)
        try {
            source.inputStream().use { input -> FileOutputStream(temporary).use { output -> input.copyTo(output); output.fd.sync() } }
            require(SafeTagWriter.sha256(temporary) == originalHash) { "Source changed while copying" }
            transform(temporary)
            RandomAccessFile(temporary, "rw").use { candidate ->
                // Bind the validator's pathname reads to this inode and byte snapshot. Merely
                // hashing after validate could bless bytes that the validator never examined.
                val unvalidated = Os.fstat(candidate.fd)
                require(OsConstants.S_ISREG(unvalidated.st_mode) && unvalidated.st_nlink == 1L && sameFile(unvalidated, Os.stat(temporary.path))) { "Candidate replaced before validation" }
                val validatedHash = hashChannel(candidate.channel)
                require(sameFile(unvalidated, Os.fstat(candidate.fd))) { "Candidate changed before validation" }
                validate(temporary)
                require(sameFile(unvalidated, Os.fstat(candidate.fd)) && sameFile(unvalidated, Os.stat(temporary.path)) && hashChannel(candidate.channel) == validatedHash) { "Candidate changed during validation" }
                // Preserve the source's ownership and permissions. A provider that does not permit
                // an equivalent sibling file gets external-file fallback, never a lossy overwrite.
                var staged = Os.stat(temporary.path)
                if (staged.st_mode and 0x1ff != before.st_mode and 0x1ff) {
                    Os.chmod(temporary.path, before.st_mode and 0x1ff)
                    staged = Os.stat(temporary.path)
                }
                require(staged.st_uid == before.st_uid && staged.st_gid == before.st_gid && staged.st_mode and 0x1ff == before.st_mode and 0x1ff) { "Cannot preserve file permissions" }
                Files.setLastModifiedTime(temporary.toPath(), FileTime.from(Instant.ofEpochSecond(before.st_mtim.tv_sec, before.st_mtim.tv_nsec)))
                val timestamp = Os.stat(temporary.path).st_mtim
                require(timestamp.tv_sec == before.st_mtim.tv_sec && timestamp.tv_nsec == before.st_mtim.tv_nsec) { "Cannot preserve modification time" }
                candidate.fd.sync()
                val validated = Os.fstat(candidate.fd)
                require(validated.st_dev == unvalidated.st_dev && validated.st_ino == unvalidated.st_ino &&
                    sameFile(validated, Os.stat(temporary.path)) && hashChannel(candidate.channel) == validatedHash) { "Candidate replaced while preserving attributes" }
                RandomAccessFile(source, "rw").use { current ->
                    // Hold the OS advisory write lock continuously across the final hash, identity
                    // checks and rename. Do not reopen/close a second source fd while locked: POSIX
                    // process locks can be released by closing another fd for the same inode.
                    val lock = requireNotNull(current.channel.tryLock()) { "Source is locked by another process" }
                    lock.use {
                        // Take this lock AFTER validation/attribute APIs have closed their own
                        // candidate fds. Closing another fd can release POSIX process locks.
                        val candidateLock = requireNotNull(candidate.channel.tryLock()) { "Candidate is locked by another process" }
                        candidateLock.use {
                            if (!canCommit()) return false
                            require(sameFile(before, Os.stat(source.path)) && sameFile(before, Os.fstat(authorizedFd)) && sameFile(before, Os.fstat(current.fd))) { "Source replaced while editing" }
                            require(hashChannel(current.channel) == originalHash) { "Source changed while editing" }
                            require(sameFile(validated, Os.fstat(candidate.fd)) && sameFile(validated, Os.stat(temporary.path))) { "Validated candidate replaced before commit" }
                            require(hashChannel(candidate.channel) == validatedHash) { "Validated candidate bytes changed before commit" }
                            require(sameFile(validated, Os.fstat(candidate.fd)) && sameFile(validated, Os.stat(temporary.path))) { "Candidate changed while checking commit" }
                            require(sameFile(before, Os.stat(source.path))) { "Source changed before commit" }
                            // Sole commit point; no copy-over fallback or retry with a stale snapshot.
                            // Advisory locks cannot exclude writers that ignore the locking protocol.
                            Os.rename(temporary.path, source.path)
                            return true
                        }
                    }
                }
            }
        } finally { temporary.delete() }
    }

    private fun hashChannel(channel: FileChannel): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteBuffer.allocate(64 * 1024)
        channel.position(0)
        while (true) {
            buffer.clear()
            val n = channel.read(buffer)
            if (n < 0) break
            buffer.flip()
            digest.update(buffer)
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun sameFile(a: StructStat, b: StructStat) = a.st_dev == b.st_dev && a.st_ino == b.st_ino &&
        a.st_size == b.st_size && a.st_mode == b.st_mode && a.st_uid == b.st_uid && a.st_gid == b.st_gid &&
        a.st_mtim.tv_sec == b.st_mtim.tv_sec && a.st_mtim.tv_nsec == b.st_mtim.tv_nsec &&
        a.st_ctim.tv_sec == b.st_ctim.tv_sec && a.st_ctim.tv_nsec == b.st_ctim.tv_nsec
}
