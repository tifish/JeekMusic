package com.tinyfish.jeekmusic.data.scanner;

import java.io.RandomAccessFile;
import java.nio.channels.FileLock;

/** Separate app_process OS process; no app service or production interface is added. */
public final class TagLockProcess {
    public static void main(String[] args) throws Exception {
        try (RandomAccessFile file = new RandomAccessFile(args[0], "rw");
             FileLock lock = file.getChannel().lock()) {
            System.out.println("LOCKED:" + android.os.Process.myPid());
            System.out.flush();
            Thread.sleep(8000);
        }
        System.out.println("RELEASED");
        System.out.flush();
        // app_process may keep Android runtime threads alive after main returns.
        System.exit(0);
    }
}
