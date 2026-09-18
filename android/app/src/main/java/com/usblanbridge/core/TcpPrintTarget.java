package com.usblanbridge.core;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;

/** Forwards jobs to another network printer, e.g. a real Epson on the LAN. Handy for testing without USB. */
public final class TcpPrintTarget implements PrintTarget {

    private final String host;
    private final int port;

    public TcpPrintTarget(String host, int port) {
        this.host = host;
        this.port = port;
    }

    @Override
    public String getName() {
        return host + ":" + port;
    }

    @Override
    public Job startJob(String documentName) throws IOException {
        Socket socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), 5000);
        socket.setSoTimeout(10000);
        return new TcpJob(socket);
    }

    private static final class TcpJob implements Job {
        private final Socket socket;
        private final OutputStream out;
        private boolean done;

        TcpJob(Socket socket) throws IOException {
            this.socket = socket;
            this.out = socket.getOutputStream();
        }

        @Override
        public void write(byte[] buffer, int offset, int count) throws IOException {
            out.write(buffer, offset, count);
        }

        @Override
        public void complete() throws IOException {
            if (done) return;
            done = true;
            try {
                out.flush();
            } finally {
                closeQuietly();
            }
        }

        @Override
        public void abort() {
            done = true;
            closeQuietly();
        }

        @Override
        public void close() throws IOException {
            complete();
        }

        private void closeQuietly() {
            try {
                socket.close();
            } catch (Throwable ignored) {
            }
        }
    }
}
