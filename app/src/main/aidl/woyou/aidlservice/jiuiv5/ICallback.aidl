package woyou.aidlservice.jiuiv5;

/**
 * Result callback for Sunmi's built-in printer service.
 *
 * Declared here rather than taken from a vendor SDK so the app carries no third-party binary. The method order
 * is part of the contract, because AIDL assigns binder transaction ids by declaration order.
 */
interface ICallback {

    /** Result of the call: true when it succeeded. */
    oneway void onRunResult(boolean isSuccess);

    /** String result, for calls that return data such as a printed length. */
    oneway void onReturnString(String result);

    /** Raised when the call failed, with a reason code and message. */
    oneway void onRaiseException(int code, String msg);

    /** Printer-side result of a print job. */
    oneway void onPrintResult(int code, String msg);
}
