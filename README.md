# Darza's Dominion Packet Proxy

A Java networking proxy for researching and reverse-engineering the network protocol used by **Darza's Dominion**.

This project sits between the game client and the official servers, allowing packets to be intercepted, logged, deserialized into structured objects, modified, reserialized, and forwarded.

## Project Status

**Archived / Dead Project**

Reverse-engineering the packet format was not the main obstacle. Decompiled game code and network captures indicated that Queue, Game, and Game_Slave all used the same packet structure and serialization logic.

The real challenge was intercepting Game and Game_Slave traffic. The Queue server traffic can be successfully redirected by modifying the game's connection target to `127.0.0.1`, but the same technique did not affect Game and Game_Slave connections. Those servers are reached through a different runtime path that I was unable to fully identify or modify.

---

## Features

- Local TCP proxy for multiple game services
- Packet logging in hexadecimal format
- Packet length handling (Little Endian / Big Endian detection)
- Packet deserialization into Java objects
- Packet modification and reserialization
- Virtual-thread based session handling (Project Loom)
- Extensible packet registry system

---

## How It Works

### 1. Proxy Architecture

The proxy is designed to sit between the game client and official servers:

```
Client → Local Proxy → Official Server
```

For each known service, a local listener forwards traffic to the corresponding remote endpoint.

---

### 2. Service Routing

The proxy defines routes for three services:

- Queue (6412)
- Game (6410)
- Game_Slave (6411)

Each route is handled independently with its own listener and forwarding logic.

---

### 3. Connection Handling

When a client connects, the proxy:

1. Accepts the incoming socket
2. Opens a connection to the remote server
3. Spawns two virtual threads:
    - Upstream (client → server)
    - Downstream (server → client)

Each thread continuously reads packets and forwards them.

---

### 4. Packet Framing

Each packet begins with a 4-byte length prefix.

The proxy reads:

```java
int payloadLength = in.readInt();
```

Game and Game_Slave use differing endianness, so a correction heuristic is applied when needed:

```java
if (payloadLength > 1_000_000) {
    payloadLength = Integer.reverseBytes(payloadLength);
}
```

After resolving the correct length, the payload is read in full.

---

### 5. Packet Structure

The first byte of every payload represents the packet ID:

```java
int packetId = payload[0] & 0xFF;
```

A registry maps packet IDs to known packet implementations.

If a packet type is recognized, it is deserialized into a structured object; otherwise it is forwarded as raw bytes.

---

### 6. Packet Processing Pipeline

For each packet:

1. Read length-prefixed payload
2. Identify packet ID
3. Optionally deserialize via `PacketRegistry`
4. Re-encode packet
5. Forward to destination

This allows inspection and modification of live traffic.

---

### 7. Varint Format

The game uses a custom variable-length integer encoding rather than standard protobuf varints.

Key characteristics:

- First byte contains:
    - 6 bits of value
    - 1 sign bit
    - 1 continuation bit
- Subsequent bytes use 7-bit continuation encoding

This format was reconstructed through analysis of decompiled serialization logic and captured traffic.

---

## Packet Registry

Example registry setup:

```java
registry.put(0x46, HealthUpdatePacket::new);
registry.put(0x42, EscapePacket::new);
```

If a packet ID is known, it is parsed into a Java class implementing:

```java
public interface Packet {
    void read(GameReader in);
    void write(GameWriter out);
}
```

---

## Example Packet

### HealthUpdatePacket

```java
public class HealthUpdatePacket implements Packet {
    public int maxHealth, health, shield, barrier;

    public void read(GameReader in) {
        maxHealth = in.readVarint();
        health = in.readVarint();
        shield = in.readVarint();
        barrier = in.readVarint();
    }
}
```

---

## Reverse Engineering Process

The packet structure was reconstructed using:

### 1. Decompiled Game Code

- Packet definitions
- Serialization logic
- Packet IDs
- Varint encoding behavior

### 2. Wireshark Analysis

- Packet boundaries
- Length fields
- Validation of reconstructed structures

Together, these sources allowed accurate reconstruction of the protocol format.

---

## Key Limitation: Connection Interception

While packet structure analysis was successful across all services, only the Queue service could be reliably proxied.

### What worked:

- Modifying the game to redirect Queue traffic to `127.0.0.1`
- Fully intercepting and forwarding Queue packets through the proxy

### What did NOT work:

- Game and Game_Slave traffic could not be reliably redirected
- Despite code modification, these services appeared to establish connections through a different runtime path or resolution mechanism
- The actual connection target for these services could not be consistently forced through localhost

This limitation prevented full proxying of the game’s networking stack.

---

## Disclaimer

This project was created for educational and research purposes only.

It explores network protocols, packet serialization, and reverse engineering techniques in a controlled environment.

It is not affiliated with or endorsed by the developers of Darza's Dominion.



