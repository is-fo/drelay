package networking;

public record ServerRoute(int localPort, String remoteIp, int remotePort, String name) {
}
