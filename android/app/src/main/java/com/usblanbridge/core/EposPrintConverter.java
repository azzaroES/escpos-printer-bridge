package com.usblanbridge.core;

import android.util.Base64;

import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;
import org.xml.sax.InputSource;

import java.io.ByteArrayOutputStream;
import java.io.StringReader;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.List;

import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;

/**
 * Converts an Epson ePOS-Print XML document into ESC/POS bytes for a generic receipt printer.
 *
 * Clean-room implementation of the published element set, namespace
 * http://www.epson-pos.com/schemas/2011/03/epos-print. No Epson code is used or redistributed.
 *
 * Port of the C# EposPrintConverter in the Windows build; keep the two in step.
 */
public final class EposPrintConverter {

    public static final String EPOS_NAMESPACE = "http://www.epson-pos.com/schemas/2011/03/epos-print";

    private final ByteArrayOutputStream out = new ByteArrayOutputStream(4096);
    private final List<String> warnings = new ArrayList<>();
    private Charset charset = pick("windows-1252");

    public static final class Result {
        public final byte[] bytes;
        public final List<String> warnings;

        Result(byte[] bytes, List<String> warnings) {
            this.bytes = bytes;
            this.warnings = warnings;
        }
    }

    /** Throws IllegalArgumentException when the document is malformed or is not an ePOS-Print document. */
    public static Result convert(String xml) {
        EposPrintConverter c = new EposPrintConverter();
        c.run(xml);
        return new Result(c.out.toByteArray(), c.warnings);
    }

    private void run(String xml) {
        Element root;
        try {
            DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
            factory.setNamespaceAware(true);
            // Do not resolve external entities.
            try {
                factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
            } catch (Exception ignored) {
            }
            factory.setExpandEntityReferences(false);
            DocumentBuilder builder = factory.newDocumentBuilder();
            Document doc = builder.parse(new InputSource(new StringReader(xml)));
            root = findEposPrint(doc);
        } catch (Exception e) {
            throw new IllegalArgumentException("Not a valid ePOS-Print document: " + e.getMessage(), e);
        }
        if (root == null) throw new IllegalArgumentException("No <epos-print> element found.");

        emit(0x1B, 0x40); // ESC @
        NodeList children = root.getChildNodes();
        for (int i = 0; i < children.getLength(); i++) {
            Node n = children.item(i);
            if (n instanceof Element) handle((Element) n);
        }
    }

    private static Element findEposPrint(Document doc) {
        NodeList byNs = doc.getElementsByTagNameNS(EPOS_NAMESPACE, "epos-print");
        if (byNs.getLength() > 0) return (Element) byNs.item(0);
        NodeList plain = doc.getElementsByTagName("epos-print");
        if (plain.getLength() > 0) return (Element) plain.item(0);
        Element root = doc.getDocumentElement();
        if (root != null && "epos-print".equalsIgnoreCase(localName(root))) return root;
        return null;
    }

    private static String localName(Node n) {
        String ln = n.getLocalName();
        return ln != null ? ln : n.getNodeName();
    }

    private void handle(Element el) {
        String name = localName(el).toLowerCase();
        if ("text".equals(name)) doText(el);
        else if ("feed".equals(name)) doFeed(el);
        else if ("cut".equals(name)) doCut(el);
        else if ("pulse".equals(name)) doPulse(el);
        else if ("barcode".equals(name)) doBarcode(el);
        else if ("symbol".equals(name)) doSymbol(el);
        else if ("image".equals(name)) doImage(el);
        else if ("command".equals(name)) doCommand(el);
        else if ("reset".equals(name) || "recovery".equals(name) || "layout".equals(name)) emit(0x1B, 0x40);
        else if ("logo".equals(name)) warnings.add("<logo> ignored; it needs a logo stored in the printer.");
        else if ("sound".equals(name)) emit(0x1B, 0x42, 0x01, 0x03);
        else warnings.add("<" + localName(el) + "> is not supported and was skipped.");
    }

    // ------------------------------------------------------------------ text

    private void doText(Element el) {
        if (has(el, "lang")) setLanguage(attr(el, "lang"));
        if (has(el, "align")) {
            String a = attr(el, "align");
            emit(0x1B, 0x61, "center".equals(a) ? 1 : "right".equals(a) ? 2 : 0);
        }
        if (has(el, "font")) emit(0x1B, 0x4D, "font_b".equals(attr(el, "font")) ? 1 : 0);
        if (has(el, "linespc")) emit(0x1B, 0x33, clamp(intAttr(el, "linespc", 30), 0, 255));
        if (has(el, "smooth")) emit(0x1D, 0x62, boolAttr(el, "smooth") ? 1 : 0);
        if (has(el, "ul")) emit(0x1B, 0x2D, boolAttr(el, "ul") ? 1 : 0);
        if (has(el, "em")) emit(0x1B, 0x45, boolAttr(el, "em") ? 1 : 0);
        if (has(el, "reverse")) emit(0x1D, 0x42, boolAttr(el, "reverse") ? 1 : 0);
        if (has(el, "rotate")) emit(0x1B, 0x56, boolAttr(el, "rotate") ? 1 : 0);

        boolean hasSize = has(el, "width") || has(el, "height");
        boolean hasDouble = has(el, "dw") || has(el, "dh");
        if (hasSize || hasDouble) {
            int w = has(el, "width") ? intAttr(el, "width", 1) : (boolAttr(el, "dw") ? 2 : 1);
            int h = has(el, "height") ? intAttr(el, "height", 1) : (boolAttr(el, "dh") ? 2 : 1);
            int n = ((clamp(w, 1, 8) - 1) << 4) | (clamp(h, 1, 8) - 1);
            emit(0x1D, 0x21, n);
        }

        if (hasCharacterData(el)) emitText(el.getTextContent());
    }

    private static boolean hasCharacterData(Element el) {
        NodeList kids = el.getChildNodes();
        for (int i = 0; i < kids.getLength(); i++) {
            short t = kids.item(i).getNodeType();
            if (t == Node.TEXT_NODE || t == Node.CDATA_SECTION_NODE) {
                String v = kids.item(i).getNodeValue();
                if (v != null && v.length() > 0) return true;
            }
        }
        return false;
    }

    private void emitText(String text) {
        if (text == null) return;
        String normalised = text.replace("\r\n", "\n").replace('\r', '\n');
        for (int i = 0; i < normalised.length(); i++) {
            char ch = normalised.charAt(i);
            if (ch == '\n') {
                out.write(0x0A);
                continue;
            }
            byte[] b = String.valueOf(ch).getBytes(charset);
            out.write(b, 0, b.length);
        }
    }

    private void setLanguage(String lang) {
        String l = lang == null ? "" : lang.toLowerCase();
        int escT;
        String cs;
        if (l.startsWith("ja")) {
            escT = 1;
            cs = "Shift_JIS";
        } else if (l.startsWith("ko")) {
            escT = 0;
            cs = "EUC-KR";
        } else if (l.startsWith("zh")) {
            escT = 0;
            cs = "GBK";
        } else {
            escT = 16;
            cs = "windows-1252";
        }
        Charset picked = pick(cs);
        if (picked != null) charset = picked;
        emit(0x1B, 0x74, escT);
    }

    private static Charset pick(String name) {
        try {
            return Charset.forName(name);
        } catch (Exception e) {
            try {
                return Charset.forName("US-ASCII");
            } catch (Exception e2) {
                return Charset.defaultCharset();
            }
        }
    }

    // ------------------------------------------------------------------ feed / cut / drawer

    private void doFeed(Element el) {
        if (has(el, "line")) emit(0x1B, 0x64, clamp(intAttr(el, "line", 1), 0, 255));
        else if (has(el, "unit")) emit(0x1B, 0x4A, clamp(intAttr(el, "unit", 1), 0, 255));
        else out.write(0x0A);
    }

    private void doCut(Element el) {
        if ("no_feed".equals(attr(el, "type"))) emit(0x1D, 0x56, 0x01);
        else emit(0x1D, 0x56, 0x42, 0x00);
    }

    private void doPulse(Element el) {
        int m = "drawer_2".equals(attr(el, "drawer")) ? 1 : 0;
        String time = attr(el, "time");
        int on = 50;
        if ("pulse_200".equals(time)) on = 100;
        else if ("pulse_300".equals(time)) on = 150;
        else if ("pulse_400".equals(time)) on = 200;
        else if ("pulse_500".equals(time)) on = 250;
        emit(0x1B, 0x70, m, on, on);
    }

    // ------------------------------------------------------------------ barcode / QR / image

    private void doBarcode(Element el) {
        String type = attr(el, "type");
        int m = barcodeType(type);
        if (m < 0) {
            warnings.add("Barcode type '" + type + "' is not supported; skipped.");
            return;
        }
        String hri = attr(el, "hri");
        int hriPos = "above".equals(hri) ? 1 : "below".equals(hri) ? 2 : "both".equals(hri) ? 3 : 0;
        emit(0x1D, 0x48, hriPos);
        if (has(el, "font")) emit(0x1D, 0x66, "font_b".equals(attr(el, "font")) ? 1 : 0);
        if (has(el, "width")) emit(0x1D, 0x77, clamp(intAttr(el, "width", 3), 2, 6));
        if (has(el, "height")) emit(0x1D, 0x68, clamp(intAttr(el, "height", 162), 1, 255));

        byte[] data = text(el).getBytes(Charset.forName("US-ASCII"));
        int len = Math.min(data.length, 255);
        emit(0x1D, 0x6B, m, len);
        out.write(data, 0, len);
    }

    private static int barcodeType(String type) {
        if (type == null) return -1;
        if ("upc_a".equals(type)) return 65;
        if ("upc_e".equals(type)) return 66;
        if ("ean13".equals(type) || "jan13".equals(type)) return 67;
        if ("ean8".equals(type) || "jan8".equals(type)) return 68;
        if ("code39".equals(type)) return 69;
        if ("itf".equals(type)) return 70;
        if ("codabar".equals(type)) return 71;
        if ("code93".equals(type)) return 72;
        if ("code128".equals(type) || "code128_auto".equals(type) || "gs1_128".equals(type)) return 73;
        return -1;
    }

    private void doSymbol(Element el) {
        String type = attr(el, "type");
        if (type != null && type.toLowerCase().startsWith("qrcode")) {
            emitQr(el);
            return;
        }
        warnings.add("2D symbol type '" + type + "' is not supported; only QR Code is. Skipped.");
    }

    private void emitQr(Element el) {
        byte[] data = text(el).getBytes(Charset.forName("UTF-8"));
        int size = has(el, "size") ? clamp(intAttr(el, "size", 3), 1, 16) : 3;
        String lvl = attr(el, "level");
        int level = "level_m".equals(lvl) ? 49 : "level_q".equals(lvl) ? 50 : "level_h".equals(lvl) ? 51 : 48;

        emit(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00); // model 2
        emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, size);       // module size
        emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, level);      // error correction

        int len = data.length + 3;
        emit(0x1D, 0x28, 0x6B, len & 0xFF, (len >> 8) & 0xFF, 0x31, 0x50, 0x30);
        out.write(data, 0, data.length);
        emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);       // print
    }

    private void doImage(Element el) {
        int width = intAttr(el, "width", 0);
        int height = intAttr(el, "height", 0);
        if (width <= 0 || height <= 0) {
            warnings.add("<image> is missing width or height; skipped.");
            return;
        }
        byte[] raster;
        try {
            raster = Base64.decode(text(el), Base64.DEFAULT);
        } catch (Exception e) {
            warnings.add("<image> data is not valid base64; skipped.");
            return;
        }
        int bytesPerRow = (width + 7) / 8;
        int expected = bytesPerRow * height;
        if (raster == null || raster.length < expected) {
            warnings.add("<image> data is shorter than width times height; skipped.");
            return;
        }
        emit(0x1D, 0x76, 0x30, 0x00);
        emit(bytesPerRow & 0xFF, (bytesPerRow >> 8) & 0xFF);
        emit(height & 0xFF, (height >> 8) & 0xFF);
        out.write(raster, 0, expected);
    }

    private void doCommand(Element el) {
        String hex = text(el).replaceAll("\\s", "");
        if (hex.length() % 2 != 0) {
            warnings.add("<command> is not valid hex; skipped.");
            return;
        }
        try {
            for (int i = 0; i < hex.length(); i += 2) {
                out.write(Integer.parseInt(hex.substring(i, i + 2), 16));
            }
        } catch (NumberFormatException e) {
            warnings.add("<command> is not valid hex; skipped.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void emit(int... bytes) {
        for (int b : bytes) out.write(b & 0xFF);
    }

    private static String text(Element el) {
        String s = el.getTextContent();
        return s == null ? "" : s;
    }

    private static boolean has(Element el, String name) {
        return el.hasAttribute(name);
    }

    private static String attr(Element el, String name) {
        return el.hasAttribute(name) ? el.getAttribute(name) : null;
    }

    private static boolean boolAttr(Element el, String name) {
        String v = attr(el, name);
        return "true".equals(v) || "1".equals(v);
    }

    private static int intAttr(Element el, String name, int fallback) {
        try {
            return Integer.parseInt(attr(el, name));
        } catch (Exception e) {
            return fallback;
        }
    }

    private static int clamp(int v, int lo, int hi) {
        return v < lo ? lo : v > hi ? hi : v;
    }
}
