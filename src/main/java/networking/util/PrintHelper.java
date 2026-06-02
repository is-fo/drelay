package networking.util;

public class PrintHelper {
    public static String bytesToHex(byte[] bytes) {
        if (bytes == null || bytes.length == 0) return "Empty";
        StringBuilder sb = new StringBuilder();
        for (byte b : bytes) {
            sb.append(String.format("%02X ", b));
        }
        return sb.toString().trim();
    }
}
