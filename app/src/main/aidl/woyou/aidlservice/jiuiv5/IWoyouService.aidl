package woyou.aidlservice.jiuiv5;

import woyou.aidlservice.jiuiv5.ICallback;

/**
 * Sunmi's built-in thermal printer service.
 *
 * IMPORTANT: AIDL assigns binder transaction ids from the ORDER of these declarations, so the order must match
 * the service exactly. Getting it wrong does not fail loudly; the service binds and printing silently does
 * nothing, which is a known trap with hand-vendored copies of this interface.
 *
 * Only the first twelve methods are declared, up to and including sendRAWData, which is the one the bridge uses.
 * Transaction ids are positional, so declaring a prefix of the real interface keeps every id we use correct
 * while avoiding guesses about the remaining methods. Do not insert or reorder anything above sendRAWData.
 *
 * Signatures taken from Sunmi's published printer interface.
 */
interface IWoyouService {

    // 1
    boolean postPrintData(String packageName, in byte[] data, int offset, int length);

    // 2
    int getFirmwareStatus();

    // 3
    String getServiceVersion();

    // 4
    void printerInit(in ICallback callback);

    // 5
    void printerSelfChecking(in ICallback callback);

    // 6
    String getPrinterSerialNo();

    // 7
    String getPrinterVersion();

    // 8
    String getPrinterModal();

    // 9
    void getPrintedLength(in ICallback callback);

    // 10
    void lineWrap(int n, in ICallback callback);

    // 11  the one that matters: raw ESC/POS straight to the print head
    void sendRAWData(in byte[] data, in ICallback callback);

    // 12
    void setAlignment(int alignment, in ICallback callback);
}
