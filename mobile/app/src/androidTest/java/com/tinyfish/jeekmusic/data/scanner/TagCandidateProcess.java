package com.tinyfish.jeekmusic.data.scanner;

import java.io.File;
import java.io.RandomAccessFile;
import java.nio.channels.FileLock;

/** Real independent Linux process, used only by instrumentation. */
public final class TagCandidateProcess {
    public static void main(String[] args) throws Exception {
        if (args[0].equals("probe")) {
            try (RandomAccessFile file = new RandomAccessFile(args[1], "rw")) {
                FileLock lock = file.getChannel().tryLock();
                System.out.println(lock == null ? "BLOCKED" : "UNPROTECTED");
                if (lock != null) lock.close();
            }
        } else if (args[0].equals("watch")) {
            File directory = new File(args[1]);
            System.out.println("WATCHING:" + android.os.Process.myPid());
            System.out.flush();
            long deadline = System.nanoTime() + 15_000_000_000L;
            File candidate = null;
            while (candidate == null && System.nanoTime() < deadline) {
                File[] files = directory.listFiles((dir, name) -> name.startsWith(".jeekmusic-tags-"));
                if (files != null && files.length == 1) candidate = files[0];
                else Thread.sleep(1);
            }
            if (candidate == null) throw new IllegalStateException("No candidate observed");
            try (RandomAccessFile file = new RandomAccessFile(candidate, "rw");
                 FileLock lock = file.getChannel().lock()) {
                System.out.println("LOCKED:" + candidate.getName());
                System.out.flush();
                System.in.read(); // Parent releases only after the repository call completes.
            }
        } else {
            File target = new File(args[1]);
            try (RandomAccessFile file = new RandomAccessFile(target, "rw");
                 FileLock lock = file.getChannel().lock()) {
                if (args[0].equals("replace-identical")) {
                    File rival = new File(target.getParentFile(), "review-external-rival.tmp");
                    java.nio.file.Files.copy(target.toPath(), rival.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                    rival.setLastModified(target.lastModified());
                    android.system.Os.rename(rival.getPath(), target.getPath());
                } else if (args[0].equals("same-size")) {
                    long timestamp = target.lastModified();
                    file.seek(file.length() - 1);
                    int value = file.read();
                    file.seek(file.length() - 1);
                    file.write(value ^ 1);
                    file.getFD().sync();
                    target.setLastModified(timestamp);
                } else {
                    file.setLength(0);
                    file.write(new byte[] {1, 2, 3});
                    file.getFD().sync();
                }
            }
            System.out.println("MUTATED:" + android.os.Process.myPid());
        }
        System.out.flush();
        System.exit(0);
    }
}
