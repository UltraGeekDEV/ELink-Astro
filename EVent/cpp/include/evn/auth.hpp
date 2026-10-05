// EVent for C++: link authentication. SHA-256, HMAC-SHA256 and the proofs of the leaf protocol's challenge-response (see AuthProtocol in the
// C# library, and "Authentication" in the wiki).
//
// Part of the portable core: C++17, no exceptions thrown, no I/O, no threads. (Nonces come from the caller: the leaf draws them from the
// system's random device.)
//
// A peer that requires authentication answers a leaf's InterconnectRunning with
//     AuthChallenge  bytes serverNonce (32) | string realm            (bytes/string: int32 length, then the bytes, UTF-8)
// and the leaf answers
//     AuthResponse   string userId | bytes clientNonce (32) | bytes proof
// where proof = HMAC-SHA256(key = UTF-8 secret, label | field(serverNonce) | field(clientNonce) | field(userId) | field(realm)), a field being a
// uint32 little-endian length and the bytes, and label "EVENT-AUTH-1 client". The peer then proves it knows the secret too:
//     AuthResult     bytes serverProof        (the same, with label "EVENT-AUTH-1 server")
// and only then sends its event list. The secret itself is never sent.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "non.hpp"

namespace evn {
namespace auth {

using Bytes = std::vector<uint8_t>;

inline constexpr size_t nonce_size = 32;
inline constexpr const char* client_label = "EVENT-AUTH-1 client";
inline constexpr const char* server_label = "EVENT-AUTH-1 server";

namespace detail {
inline constexpr uint32_t K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be,
    0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa,
    0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85,
    0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
    0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f,
    0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

inline uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
}  // namespace detail

/// SHA-256 of a byte range: 32 bytes.
inline Bytes sha256(const uint8_t* data, size_t size) {
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    Bytes message(data, data + size);
    message.push_back(0x80);
    while (message.size() % 64 != 56) message.push_back(0);
    uint64_t bits = static_cast<uint64_t>(size) * 8;
    for (int i = 7; i >= 0; i--) message.push_back(static_cast<uint8_t>(bits >> (8 * i)));
    for (size_t offset = 0; offset < message.size(); offset += 64) {
        uint32_t w[64];
        for (int i = 0; i < 16; i++) {
            const uint8_t* p = &message[offset + static_cast<size_t>(i) * 4];
            w[i] = (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) | (static_cast<uint32_t>(p[2]) << 8) | p[3];
        }
        for (int i = 16; i < 64; i++) {
            uint32_t s0 = detail::rotr(w[i - 15], 7) ^ detail::rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = detail::rotr(w[i - 2], 17) ^ detail::rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; i++) {
            uint32_t t1 = hh + (detail::rotr(e, 6) ^ detail::rotr(e, 11) ^ detail::rotr(e, 25)) + ((e & f) ^ (~e & g)) + detail::K[i] + w[i];
            uint32_t t2 = (detail::rotr(a, 2) ^ detail::rotr(a, 13) ^ detail::rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
            hh = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }
        h[0] += a;
        h[1] += b;
        h[2] += c;
        h[3] += d;
        h[4] += e;
        h[5] += f;
        h[6] += g;
        h[7] += hh;
    }
    Bytes out;
    for (uint32_t x : h) {
        for (int i = 3; i >= 0; i--) out.push_back(static_cast<uint8_t>(x >> (8 * i)));
    }
    return out;
}

inline Bytes sha256(const Bytes& data) { return sha256(data.data(), data.size()); }

/// HMAC-SHA256 of `data` under `key`: 32 bytes.
inline Bytes hmac_sha256(Bytes key, const Bytes& data) {
    if (key.size() > 64) key = sha256(key);
    key.resize(64, 0);
    Bytes inner(64), outer(64);
    for (size_t i = 0; i < 64; i++) {
        inner[i] = key[i] ^ 0x36;
        outer[i] = key[i] ^ 0x5c;
    }
    inner.insert(inner.end(), data.begin(), data.end());
    Bytes inner_hash = sha256(inner);
    outer.insert(outer.end(), inner_hash.begin(), inner_hash.end());
    return sha256(outer);
}

inline Bytes to_bytes(const std::string& s) { return Bytes(s.begin(), s.end()); }

/// The proof a leaf sends (label client_label) or a router answers with (server_label).
inline Bytes proof(const char* label, const std::string& secret, const Bytes& server_nonce, const Bytes& client_nonce, const std::string& user_id,
                   const std::string& realm) {
    Writer w;
    std::string text(label);
    w.bytes(text.data(), text.size());
    for (const Bytes& field : {server_nonce, client_nonce, to_bytes(user_id), to_bytes(realm)}) {
        w.le(static_cast<uint32_t>(field.size()));
        w.bytes(field.data(), field.size());
    }
    return hmac_sha256(to_bytes(secret), w.out);
}

/// Equal, in time that does not depend on where they differ.
inline bool same(const Bytes& a, const Bytes& b) {
    uint8_t difference = a.size() == b.size() ? 0 : 1;
    for (size_t i = 0; i < a.size() && i < b.size(); i++) difference |= static_cast<uint8_t>(a[i] ^ b[i]);
    return difference == 0;
}

}  // namespace auth
}  // namespace evn
