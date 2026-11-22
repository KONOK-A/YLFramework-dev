import com.google.crypto.tink.subtle.AesGcmHkdfStreaming;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.Arrays;

public final class TinkOracle {
    public static void main(String[] args) throws Exception {
        byte[] key = Files.readAllBytes(Path.of(args[1]));
        byte[] aad = Files.readAllBytes(Path.of(args[2]));
        try {
            var tink = new AesGcmHkdfStreaming(key, "HmacSha256", 32, 1024 * 1024, 0);
            var source = Path.of(args[3]);
            var destination = Path.of(args[4]);
            if (args[0].equals("seal")) {
                try (InputStream plain = Files.newInputStream(source);
                     OutputStream out = tink.newEncryptingStream(Files.newOutputStream(destination, StandardOpenOption.CREATE_NEW), aad)) {
                    plain.transferTo(out);
                }
            } else if (args[0].equals("open")) {
                try (InputStream in = tink.newDecryptingStream(Files.newInputStream(source), aad);
                     OutputStream plain = Files.newOutputStream(destination, StandardOpenOption.CREATE_NEW)) {
                    in.transferTo(plain);
                }
            } else {
                throw new IllegalArgumentException("Unknown oracle operation");
            }
        } finally {
            Arrays.fill(key, (byte) 0);
        }
    }
}
