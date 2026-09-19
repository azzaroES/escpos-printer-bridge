import com.usblanbridge.core.License;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.PrivateKey;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.PKCS8EncodedKeySpec;
import java.text.SimpleDateFormat;
import java.util.Base64;
import java.util.Date;
import java.util.Locale;

/**
 * Makes licence keys for the app. Run it through tools/run-keygen.cmd.
 *
 *   keypair <folder>                                    writes private.key and public.key; paste public.key into License.PUBLIC_KEY
 *   sign <private.key> <licensee> <device-id> [device-id]   prints a key for that licensee, valid on those devices only (max 2)
 *   check <key>                                         says whether a key is genuine, who it names and which devices
 *
 * The device id is shown in the app under "Ticket footer", with a Copy button. Quote the licensee if it has spaces.
 * The private key is the whole business: whoever holds it can make keys. It must never enter the repository.
 */
public final class LicenseKeygen {

    public static void main(String[] args) throws Exception {
        if (args.length >= 2 && args[0].equals("keypair")) {
            keypair(new File(args[1]));
        } else if (args.length >= 4 && args[0].equals("sign")) {
            String[] devices = new String[args.length - 3];
            System.arraycopy(args, 3, devices, 0, devices.length);
            sign(new File(args[1]), args[2], devices);
        } else if (args.length >= 2 && args[0].equals("check")) {
            check(args[1]);
        } else {
            System.out.println("Usage:");
            System.out.println("  run-keygen keypair <folder>");
            System.out.println("  run-keygen sign <folder>\\private.key \"<licensee>\" <device-id> [second-device-id]");
            System.out.println("  run-keygen check <key>");
            System.exit(2);
        }
    }

    private static void keypair(File dir) throws Exception {
        File priv = new File(dir, "private.key");
        File pub = new File(dir, "public.key");
        if (priv.exists()) {
            System.out.println(priv + " already exists. Refusing to overwrite a private key.");
            System.exit(1);
        }
        if (!dir.isDirectory() && !dir.mkdirs()) throw new IOException("Cannot create " + dir);

        KeyPairGenerator g = KeyPairGenerator.getInstance("EC");
        g.initialize(new ECGenParameterSpec("secp256r1"));
        KeyPair kp = g.generateKeyPair();
        String privateText = Base64.getEncoder().encodeToString(kp.getPrivate().getEncoded());
        String publicText = Base64.getEncoder().encodeToString(kp.getPublic().getEncoded());
        write(priv, privateText);
        write(pub, publicText);

        System.out.println("Private key: " + priv + "   (keep this secret and backed up)");
        System.out.println("Public key:  " + pub);
        System.out.println();
        System.out.println("Paste this into License.PUBLIC_KEY:");
        System.out.println(publicText);
    }

    private static void sign(File privateKeyFile, String licensee, String[] devices) throws Exception {
        if (devices.length > License.MAX_DEVICES) {
            System.out.println("A key covers at most " + License.MAX_DEVICES + " devices; " + devices.length + " were given.");
            System.exit(1);
        }
        String text = new String(Files.readAllBytes(privateKeyFile.toPath()), StandardCharsets.UTF_8).trim();
        PrivateKey pk = KeyFactory.getInstance("EC").generatePrivate(new PKCS8EncodedKeySpec(License.decode(text)));

        String today = new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new Date());
        byte[] payload = License.payloadFor(licensee.trim(), today, devices);
        Signature s = Signature.getInstance("SHA256withECDSA");
        s.initSign(pk);
        s.update(payload);
        String key = License.format(payload, s.sign());

        License.Info info = License.inspect(key);
        System.out.println(key);
        System.out.println();
        if (info == null) {
            System.out.println("WARNING: the app's embedded PUBLIC_KEY does not match this private key, "
                    + "so the app would reject this key. Paste the matching public.key into License.java.");
            System.exit(1);
        }
        System.out.println("Checked against the embedded public key: licensed to " + info.licensee
                + ", issued " + info.issued + ", devices " + String.join(", ", info.devices) + ".");
    }

    private static void check(String key) {
        License.Info info = License.inspect(key);
        if (info == null) {
            System.out.println("NOT VALID");
            System.exit(1);
        }
        System.out.println("Valid: licensed to " + info.licensee + ", issued " + info.issued
                + ", devices " + String.join(", ", info.devices) + ".");
    }

    private static void write(File file, String text) throws IOException {
        try (FileOutputStream out = new FileOutputStream(file)) {
            out.write((text + "\n").getBytes(StandardCharsets.UTF_8));
        }
    }
}
