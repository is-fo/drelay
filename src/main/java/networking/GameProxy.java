import networking.GameReader;
import networking.GameWriter;
import networking.ServerRoute;
import networking.packets.Packet;
import networking.packets.PacketRegistry;

import static networking.util.PrintHelper.bytesToHex;

void main() {
    PacketRegistry.init();

    String queueIp = "";
    try {
        queueIp = InetAddress.getByName("queue.playdarzas.com").getHostAddress();
        System.out.println(queueIp);
    } catch (Exception e) {
        System.err.println("Could not resolve Queue IP");
    }

    List<ServerRoute> ROUTES = List.of(
            new ServerRoute(6410, "18.206.229.19", 6410, "Game"),
            new ServerRoute(6411, "44.222.253.93", 6411, "Game_Slave"),
            new ServerRoute(6412, queueIp, 6412, "Queue"));

    for (ServerRoute route : ROUTES) {
        Thread.startVirtualThread(() -> startListener(route));
    }

    try {
        Thread.sleep(500);
        IO.println("Launching game client...");
        ProcessBuilder pb = new ProcessBuilder("E:\\SteamLibrary\\steamapps\\common\\Darza's Dominion\\DarzasDominion.exe");
        pb.directory(new File("E:\\SteamLibrary\\steamapps\\common\\Darza's Dominion"));
        pb.start();
    }
    catch (Exception e) {
        System.err.println("Failed to launch game: " + e.getMessage());
    }

    try {
        Thread.currentThread().join();
    }
    catch (InterruptedException e) {
        e.printStackTrace();
    }
}

private static void startListener(ServerRoute route) {
    try (var serverSocket = new ServerSocket(route.localPort())) {
        IO.println("Started proxy for " + route.name() + " on port " + route.localPort());

        while (true) {
            var clientSocket = serverSocket.accept();
            IO.println("[" + route.name() + "] Client connected!");
            Thread.startVirtualThread(() -> handleSession(clientSocket, route));
        }
    }
    catch (IOException e) {
        System.err.println("Failed to start listener for " + route.name() + ": " + e.getMessage());
    }
}

private static void handleSession(Socket clientSocket, ServerRoute route) {
    try (clientSocket;
         var serverSocket = new Socket(route.remoteIp(), route.remotePort())) {

        var clientIn = new GameReader(clientSocket.getInputStream());
        var clientOut = new GameWriter(clientSocket.getOutputStream());

        var serverIn = new GameReader(serverSocket.getInputStream());
        var serverOut = new GameWriter(serverSocket.getOutputStream());

        // --- UPSTREAM (Client -> Server) ---
        var upstream = Thread.startVirtualThread(() -> pumpStreams(clientIn, serverOut, route.name() + " Upstream"));

        // --- DOWNSTREAM (Server -> Client) ---
        var downstream = Thread.startVirtualThread(() -> pumpStreams(serverIn, clientOut, route.name() + " Downstream"));

        upstream.join();
        downstream.join();
    }
    catch (Exception e) {
        System.err.println("[" + route.name() + "] Session error: " + e.getMessage());
    }
}

private static void pumpStreams(GameReader in, GameWriter out, String direction) {
    try {
        while (true) {
            // read 4-bytes length prefix (LE for Queue packets, BE for others)
            int payloadLength = in.readInt();
            if (payloadLength > 1_000_000) {
                payloadLength = Integer.reverseBytes(payloadLength);
            }

            if (payloadLength < 0) {
                throw new RuntimeException("Negative payload length.");
            }

            byte[] payload = new byte[payloadLength];
            in.readFully(payload);

            System.out.printf("[%s] Length: %d | Data: %s%n",
                    direction,
                    payloadLength,
                    bytesToHex(payload)
            );

            if (payload.length == 0) {
                continue;
            }

            int packetId = payload[0] & 0xFF;
            Packet packetTemplate = PacketRegistry.createOrNull(packetId);

            byte[] payloadToWrite;

            if (packetTemplate != null) {
                ByteArrayInputStream bais = new ByteArrayInputStream(payload, 1, payload.length - 1);
                GameReader payloadReader = new GameReader(bais);
                packetTemplate.read(payloadReader);

                ByteArrayOutputStream baos = new ByteArrayOutputStream();
                GameWriter payloadWriter = new GameWriter(baos);
                payloadWriter.writeByte(packetId);
                packetTemplate.write(payloadWriter);
                payloadToWrite = baos.toByteArray();
            } else {
                payloadToWrite = payload;
            }

            synchronized (out) {
                out.writeIntLE(payloadToWrite.length);
                out.write(payloadToWrite);
                out.flush();
            }
        }
    }
    catch (EOFException e) {
        IO.println("[" + direction + "] Disconnected cleanly.");
    }
    catch (Exception e) {
        System.err.println("[" + direction + "] Stream error: " + e.getMessage());
    }
}

