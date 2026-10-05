// EVent for C++: transports. A leaf talks to its router over a byte stream; this is the interface, and the TCP
// implementation (POSIX sockets, or Winsock on Windows) used for the local loopback link today. Shared memory will be
// another implementation of the same interface.
#pragma once

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#ifdef _MSC_VER
#pragma comment(lib, "ws2_32.lib")
#endif
#else
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>
#endif

namespace evn {

/// A reliable, ordered byte stream to the router.
class Transport {
public:
    virtual ~Transport() = default;
    /// Sends all bytes; false if the stream is broken. May be called from several threads (the leaf serializes calls).
    virtual bool send(const uint8_t* data, size_t size) = 0;
    /// Blocks until some bytes arrive: their count, 0 when the stream ended, negative on an error.
    virtual long receive(uint8_t* buffer, size_t capacity) = 0;
    /// Ends the stream; a blocked receive returns. Safe to call more than once and from another thread.
    virtual void close() = 0;
};

/// TCP, typically to the router on the same machine (127.0.0.1).
class TcpTransport : public Transport {
public:
#ifdef _WIN32
    using Socket = SOCKET;
    static constexpr Socket invalid = INVALID_SOCKET;
#else
    using Socket = int;
    static constexpr Socket invalid = -1;
#endif

    /// Dials host:port. Null (with the reason in `error`) if that fails.
    static std::unique_ptr<TcpTransport> connect(const std::string& host, uint16_t port, std::string* error = nullptr) {
#ifdef _WIN32
        static const bool started = [] {
            WSADATA data;
            return WSAStartup(MAKEWORD(2, 2), &data) == 0;
        }();
        if (!started) {
            if (error) *error = "Winsock didn't start";
            return nullptr;
        }
#endif
        addrinfo hints{};
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        addrinfo* found = nullptr;
        int rc = ::getaddrinfo(host.c_str(), std::to_string(port).c_str(), &hints, &found);
        if (rc != 0) {
            if (error) *error = "can't resolve " + host;
            return nullptr;
        }
        Socket fd = invalid;
        for (addrinfo* a = found; a != nullptr; a = a->ai_next) {
#ifdef _WIN32
            fd = ::socket(a->ai_family, a->ai_socktype, a->ai_protocol);
#else
            fd = ::socket(a->ai_family, a->ai_socktype | SOCK_CLOEXEC, a->ai_protocol);
#endif
            if (fd == invalid) continue;
            if (::connect(fd, a->ai_addr, static_cast<int>(a->ai_addrlen)) == 0) break;
            close_socket(fd);
            fd = invalid;
        }
        ::freeaddrinfo(found);
        if (fd == invalid) {
            if (error) *error = "can't connect to " + host + ":" + std::to_string(port);
            return nullptr;
        }
        int one = 1;
        ::setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&one), sizeof one);  // frames are small, latency matters
        return std::unique_ptr<TcpTransport>(new TcpTransport(fd));
    }

    ~TcpTransport() override {
        close();
        std::lock_guard<std::mutex> lock(mutex_);
        if (fd_ != invalid && !handle_closed_) close_socket(fd_);
        fd_ = invalid;
    }

    bool send(const uint8_t* data, size_t size) override {
        while (size > 0) {
#ifdef _WIN32
            int sent = ::send(fd_, reinterpret_cast<const char*>(data), static_cast<int>(size > 0x40000000 ? 0x40000000 : size), 0);
#else
            ssize_t sent = ::send(fd_, data, size, MSG_NOSIGNAL);
#endif
            if (sent <= 0) return false;
            data += sent;
            size -= static_cast<size_t>(sent);
        }
        return true;
    }

    long receive(uint8_t* buffer, size_t capacity) override {
#ifdef _WIN32
        return static_cast<long>(::recv(fd_, reinterpret_cast<char*>(buffer), static_cast<int>(capacity > 0x40000000 ? 0x40000000 : capacity), 0));
#else
        return static_cast<long>(::recv(fd_, buffer, capacity, 0));
#endif
    }

    void close() override {
        std::lock_guard<std::mutex> lock(mutex_);
#ifdef _WIN32
        // shutdown() alone does not wake a thread blocked in recv() on Windows (it stays blocked until the peer closes its end), so a
        // leaf that was asked to stop would wait for the router. Closing the handle does: the blocked recv fails at once. The destructor
        // must then not close it again.
        if (fd_ != invalid && !closed_) {
            ::shutdown(fd_, SD_BOTH);
            ::closesocket(fd_);
            handle_closed_ = true;
        }
#else
        if (fd_ != invalid && !closed_) ::shutdown(fd_, SHUT_RDWR);  // wakes a blocked recv; the socket is closed on destruction
#endif
        closed_ = true;
    }

private:
    explicit TcpTransport(Socket fd) : fd_(fd) {}
    static void close_socket(Socket fd) {
#ifdef _WIN32
        ::closesocket(fd);
#else
        ::close(fd);
#endif
    }
    Socket fd_;
    bool closed_ = false;
    bool handle_closed_ = false;  // Windows: close() closed the handle itself
    std::mutex mutex_;
};

}  // namespace evn
