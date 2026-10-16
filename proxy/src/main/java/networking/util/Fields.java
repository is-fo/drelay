package networking.util;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * An ordered string-keyed map that serializes as a JSON object with no ceremony.
 *
 * <p>Events and API responses both need "a bag of named fields", and writing
 * {@code Map.of("a",1,"b",2)} at every call site is noise that also loses insertion order, which is
 * the thing that makes a log line readable. {@code new Fields().add("health", 100).add("max", 200)}
 * reads as the wire order, which is exactly the order a protocol analysis wants to see.
 *
 * <p>It <strong>delegates</strong> to a {@link LinkedHashMap} rather than extending one. Extending
 * would inherit {@code Serializable}, and everything in this relay's API surface would then be
 * silently serializable - a property nobody asked for, that no test covers, and that a future
 * {@code writeObject} on an event carrying a socket would turn into a bug. Composition also keeps the
 * fluent {@code add} methods separate from the map contract.
 */
public final class Fields implements Map<String, Object> {

    private final Map<String, Object> values = new LinkedHashMap<>();

    private Fields() {
    }

    public static Fields of() {
        return new Fields();
    }

    public Fields add(String key, Object value) {
        if (value != null) {
            values.put(key, value);
        }
        return this;
    }

    /** Adds a field only when the condition holds, so a caller can avoid a conditional block per field. */
    public Fields addIf(boolean condition, String key, Object value) {
        return condition ? add(key, value) : this;
    }

    public Fields addAll(Map<String, ?> other) {
        if (other != null) {
            other.forEach(this::add);
        }
        return this;
    }

    // --- Map ---------------------------------------------------------------------------------

    @Override
    public int size() {
        return values.size();
    }

    @Override
    public boolean isEmpty() {
        return values.isEmpty();
    }

    @Override
    public boolean containsKey(Object key) {
        return values.containsKey(key);
    }

    @Override
    public boolean containsValue(Object value) {
        return values.containsValue(value);
    }

    @Override
    public Object get(Object key) {
        return values.get(key);
    }

    @Override
    public Object put(String key, Object value) {
        return values.put(key, value);
    }

    @Override
    public Object remove(Object key) {
        return values.remove(key);
    }

    @Override
    public void putAll(Map<? extends String, ?> other) {
        values.putAll(other);
    }

    @Override
    public void clear() {
        values.clear();
    }

    @Override
    public java.util.Set<String> keySet() {
        return values.keySet();
    }

    @Override
    public java.util.Collection<Object> values() {
        return values.values();
    }

    @Override
    public java.util.Set<Entry<String, Object>> entrySet() {
        return values.entrySet();
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof Map<?, ?> map && values.equals(map);
    }

    @Override
    public int hashCode() {
        return values.hashCode();
    }

    @Override
    public String toString() {
        return values.toString();
    }
}
