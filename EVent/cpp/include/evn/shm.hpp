// EVent for C++: the shared-memory transport. A leaf can link to a node in another process of the same machine through a memory-mapped
// file holding two byte rings, one per direction, carrying the same frames as the TCP link. The layout, the rendezvous (a registry folder
// of server records and request files) and the rules are in EVent/Connections/SharedMemory/README.md; this is the dialing side (side A).
// Windows is tested; the POSIX branches (mmap, kill(pid,0), timed polling instead of named events) follow the same layout.
#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <memory>
#include <mutex>
#include <random>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "transport.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dirent.h>
#include <fcntl.h>
#include <signal.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace evn {
namespace shm {

inline constexpr uint32_t magic = 0x4D535645;  // "EVSM"
inline constexpr uint32_t version = 1;
inline constexpr size_t header_size = 64;
inline constexpr size_t ring_control_size = 192;
inline constexpr size_t data_offset = 4096;
inline constexpr uint32_t min_capacity = 4096;
inline constexpr uint32_t max_capacity = 64u * 1024 * 1024;
inline constexpr uint32_t default_capacity = 256 * 1024;
// Header fields.
inline constexpr size_t off_magic = 0, off_version = 4, off_capacity = 8, off_pid_a = 16, off_pid_b = 20, off_flags = 24;
// Ring control fields.
inline constexpr size_t off_head = 0, off_tail = 64, off_reader_waiting = 128, off_writer_waiting = 132;
inline constexpr uint32_t flag_closed_a = 1, flag_closed_b = 2, flag_attached_b = 4;
inline constexpr int safety_wait_ms = 40;

inline std::string default_root() {
#ifdef _WIN32
    wchar_t buffer[MAX_PATH + 2];
    DWORD n = ::GetTempPathW(MAX_PATH + 1, buffer);
    std::string temp;
    if (n > 0 && n <= MAX_PATH) {
        int bytes = ::WideCharToMultiByte(CP_UTF8, 0, buffer, static_cast<int>(n), nullptr, 0, nullptr, nullptr);
        temp.assign(static_cast<size_t>(bytes), '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, buffer, static_cast<int>(n), &temp[0], bytes, nullptr, nullptr);
    }
    if (!temp.empty() && (temp.back() == '\\' || temp.back() == '/')) temp.pop_back();
    return temp + "\\EVent\\shm";
#else
    struct stat st;
    if (::stat("/dev/shm", &st) == 0 && S_ISDIR(st.st_mode)) return "/dev/shm/EVent";
    const char* tmp = std::getenv("TMPDIR");
    std::string base = tmp && *tmp ? tmp : "/tmp";
    if (base.size() > 1 && base.back() == '/') base.pop_back();
    return base + "/EVent/shm";
#endif
}

inline std::string join_path(const std::string& a, const std::string& b) {
#ifdef _WIN32
    return a + "\\" + b;
#else
    return a + "/" + b;
#endif
}

#ifdef _WIN32
inline std::wstring wide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = ::MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), &w[0], n);
    return w;
}
#endif

inline bool process_alive(uint32_t pid) {
    if (pid == 0) return false;
#ifdef _WIN32
    HANDLE h = ::OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return ::GetLastError() == ERROR_ACCESS_DENIED;  // exists, but not ours to look at
    bool alive = ::WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
    ::CloseHandle(h);
    return alive;
#else
    return ::kill(static_cast<pid_t>(pid), 0) == 0 || errno == EPERM;
#endif
}

inline uint32_t current_pid() {
#ifdef _WIN32
    return ::GetCurrentProcessId();
#else
    return static_cast<uint32_t>(::getpid());
#endif
}

inline bool make_directories(const std::string& path) {
    // Creates every missing level.
    for (size_t i = 1; i <= path.size(); i++) {
        if (i == path.size() || path[i] == '/' || path[i] == '\\') {
            std::string part = path.substr(0, i);
#ifdef _WIN32
            if (part.size() == 2 && part[1] == ':') continue;
            ::CreateDirectoryW(wide(part).c_str(), nullptr);
#else
            ::mkdir(part.c_str(), 0777);
#endif
        }
    }
#ifdef _WIN32
    DWORD attrs = ::GetFileAttributesW(wide(path).c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    return ::stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
#endif
}

inline std::vector<std::string> list_files(const std::string& directory, const std::string& extension) {
    std::vector<std::string> names;
#ifdef _WIN32
    WIN32_FIND_DATAW data;
    HANDLE h = ::FindFirstFileW(wide(join_path(directory, "*" + extension)).c_str(), &data);
    if (h == INVALID_HANDLE_VALUE) return names;
    do {
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        int n = ::WideCharToMultiByte(CP_UTF8, 0, data.cFileName, -1, nullptr, 0, nullptr, nullptr);
        std::string name(static_cast<size_t>(n > 0 ? n - 1 : 0), '\0');
        if (n > 1) ::WideCharToMultiByte(CP_UTF8, 0, data.cFileName, -1, &name[0], n, nullptr, nullptr);
        if (name.size() > extension.size() && name.compare(name.size() - extension.size(), extension.size(), extension) == 0) names.push_back(name);
    } while (::FindNextFileW(h, &data));
    ::FindClose(h);
#else
    DIR* d = ::opendir(directory.c_str());
    if (!d) return names;
    while (dirent* e = ::readdir(d)) {
        std::string name = e->d_name;
        if (name.size() > extension.size() && name.compare(name.size() - extension.size(), extension.size(), extension) == 0) names.push_back(name);
    }
    ::closedir(d);
#endif
    return names;
}

inline void remove_file(const std::string& path) {
#ifdef _WIN32
    ::DeleteFileW(wide(path).c_str());
#else
    ::unlink(path.c_str());
#endif
}

/// A server record of the registry: who listens, and the tags it carries.
struct ServerInfo {
    std::string name;
    uint32_t pid = 0;
    long long started_ms = 0;
    std::vector<std::string> tags;
};

inline bool read_record(const std::string& path, ServerInfo& info) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return false;
    std::string line;
    if (!std::getline(in, line) || line != "EVENT-SHM 1") {
        if (line != "EVENT-SHM 1\r") return false;
    }
    info = ServerInfo{};
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        size_t eq = line.find('=');
        if (eq == std::string::npos || eq == 0) continue;
        std::string key = line.substr(0, eq), value = line.substr(eq + 1);
        if (key == "name") info.name = value;
        else if (key == "pid") info.pid = static_cast<uint32_t>(std::strtoul(value.c_str(), nullptr, 10));
        else if (key == "started") info.started_ms = std::strtoll(value.c_str(), nullptr, 10);
        else if (key == "tag") info.tags.push_back(value);
    }
    return !info.name.empty() && info.pid != 0;
}

/// Every live server under `root`, oldest first (records of dead processes are removed).
inline std::vector<ServerInfo> list_servers(const std::string& root) {
    std::vector<ServerInfo> found;
    std::string directory = join_path(root, "servers");
    for (const auto& file : list_files(directory, ".srv")) {
        ServerInfo info;
        std::string path = join_path(directory, file);
        if (!read_record(path, info)) continue;
        if (!process_alive(info.pid)) {
            remove_file(path);
            continue;
        }
        found.push_back(info);
    }
    std::stable_sort(found.begin(), found.end(), [](const ServerInfo& a, const ServerInfo& b) {
        return a.started_ms != b.started_ms ? a.started_ms < b.started_ms : a.name < b.name;
    });
    return found;
}

/// The oldest server matching `pattern`: a name, `tag:xyz`, `name:prefix` or `*`. Empty name when none.
inline std::string find_server(const std::string& root, const std::string& pattern) {
    for (const auto& s : list_servers(root)) {
        if (pattern.empty() || pattern == "*") return s.name;
        if (pattern.rfind("tag:", 0) == 0) {
            for (const auto& t : s.tags)
                if (t == pattern.substr(4)) return s.name;
        } else if (pattern.rfind("name:", 0) == 0) {
            if (s.name.rfind(pattern.substr(5), 0) == 0) return s.name;
        } else if (s.name == pattern) {
            return s.name;
        }
    }
    return std::string();
}

/// A wake-up between processes: a named event on Windows, nothing elsewhere (the waiter polls).
class Doorbell {
public:
    explicit Doorbell(const std::string& name) {
#ifdef _WIN32
        handle_ = ::CreateEventW(nullptr, FALSE, FALSE, wide("Local\\" + name).c_str());
#else
        (void)name;
#endif
    }
    ~Doorbell() {
#ifdef _WIN32
        if (handle_) ::CloseHandle(handle_);
#endif
    }
    Doorbell(const Doorbell&) = delete;
    Doorbell& operator=(const Doorbell&) = delete;
    void set() {
#ifdef _WIN32
        if (handle_) ::SetEvent(handle_);
#endif
    }
    void wait(int ms) {
#ifdef _WIN32
        if (handle_) {
            ::WaitForSingleObject(handle_, static_cast<DWORD>(ms));
            return;
        }
#endif
        std::this_thread::sleep_for(std::chrono::milliseconds(ms > 1 ? 1 : ms));
    }

private:
#ifdef _WIN32
    HANDLE handle_ = nullptr;
#endif
};

}  // namespace shm

/// The link to a node in another process through shared memory. Side A: this end dialed.
class ShmTransport : public Transport {
public:
    /// Dials the server `target` (a name, `tag:xyz`, `name:prefix` or `*`) and waits for it to take the link. Null (reason in `error`) if it can't.
    static std::unique_ptr<ShmTransport> connect(const std::string& target, std::string root = std::string(), std::string* error = nullptr,
                                                 uint32_t ring_bytes = shm::default_capacity, std::chrono::milliseconds timeout = std::chrono::milliseconds(5000)) {
        auto fail = [&](const std::string& why) -> std::unique_ptr<ShmTransport> {
            if (error) *error = why;
            return nullptr;
        };
        if (root.empty()) root = shm::default_root();
        std::string name = shm::find_server(root, target);
        if (name.empty()) return fail("no shared-memory server '" + target + "' in " + root);
        uint32_t capacity = shm::min_capacity;
        while (capacity < ring_bytes && capacity < shm::max_capacity) capacity <<= 1;
        if (!shm::make_directories(shm::join_path(root, "links")) || !shm::make_directories(shm::join_path(root, "requests")))
            return fail("can't create the shared-memory folders in " + root);
        std::string id = new_id();
        std::string link_path = shm::join_path(shm::join_path(root, "links"), id + ".shm");
        std::unique_ptr<ShmTransport> link(new ShmTransport(id, capacity));
        if (!link->create(link_path)) return fail("can't create the link file " + link_path);
        // The request: written whole, then renamed, so the server never reads half of it.
        std::string request = shm::join_path(shm::join_path(root, "requests"), name + "." + id + ".req");
        {
            std::ofstream out(request + ".tmp", std::ios::binary | std::ios::trunc);
            out << "pid=" << shm::current_pid() << "\n";
        }
#ifdef _WIN32
        bool moved = ::MoveFileExW(shm::wide(request + ".tmp").c_str(), shm::wide(request).c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
#else
        bool moved = ::rename((request + ".tmp").c_str(), request.c_str()) == 0;
#endif
        if (!moved) return fail("can't write the connection request");
        {
            shm::Doorbell server_bell("EVentShmSrv_" + name);
            server_bell.set();
        }
        auto started = std::chrono::steady_clock::now();
        while (!link->attached()) {
            if (std::chrono::steady_clock::now() - started > timeout || shm::find_server(root, name).empty()) {
                shm::remove_file(request);
                return fail("the server '" + name + "' didn't take the link");
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        return link;
    }

    ~ShmTransport() override {
        close();
        unmap();
    }

    bool send(const uint8_t* data, size_t size) override {
        while (size > 0) {
            if (closing_.load()) return false;
            size_t n = try_write(data, size);
            if (n > 0) {
                data += n;
                size -= n;
                continue;
            }
            if (peer_gone()) return false;
            announce(false);
            if (writable() > 0) {
                withdraw(false);
                continue;
            }
            bell(out_ring() * 2 + 1)->wait(shm::safety_wait_ms);
            withdraw(false);
        }
        return true;
    }

    long receive(uint8_t* buffer, size_t capacity) override {
        while (true) {
            if (closing_.load()) return 0;
            size_t n = try_read(buffer, capacity);
            if (n > 0) return static_cast<long>(n);
            if (peer_gone()) return static_cast<long>(try_read(buffer, capacity));   // what the peer left, then the end
            announce(true);
            if (readable() > 0) {
                withdraw(true);
                continue;
            }
            bell(in_ring() * 2)->wait(shm::safety_wait_ms);
            withdraw(true);
        }
    }

    void close() override {
        if (closing_.exchange(true)) return;
        if (base_) or_flags(shm::flag_closed_a);
        for (int i = 0; i < 4; i++)
            if (bells_[i]) bells_[i]->set();
    }

private:
    ShmTransport(std::string id, uint32_t capacity) : id_(std::move(id)), capacity_(capacity) {
        for (int i = 0; i < 4; i++) bells_[i].reset(new shm::Doorbell("EVentShm_" + id_ + "_" + std::to_string(i)));
    }

    static std::string new_id() {
        std::random_device rd;
        std::mt19937_64 gen((static_cast<uint64_t>(rd()) << 32) ^ rd() ^ static_cast<uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count()));
        char text[33];
        std::snprintf(text, sizeof text, "%016llx%016llx", static_cast<unsigned long long>(gen()), static_cast<unsigned long long>(gen()));
        return text;
    }

    // Ring 0 carries A->B, ring 1 B->A. This side is A: it writes ring 0 and reads ring 1.
    static constexpr int out_ring() { return 0; }
    static constexpr int in_ring() { return 1; }
    shm::Doorbell* bell(int i) { return bells_[i].get(); }
    uint8_t* control(int ring) const { return base_ + shm::header_size + static_cast<size_t>(ring) * shm::ring_control_size; }
    uint8_t* data(int ring) const { return base_ + shm::data_offset + static_cast<size_t>(ring) * capacity_; }
    static std::atomic<uint64_t>& u64(uint8_t* p) { return *reinterpret_cast<std::atomic<uint64_t>*>(p); }
    static std::atomic<uint32_t>& u32(uint8_t* p) { return *reinterpret_cast<std::atomic<uint32_t>*>(p); }

    bool create(const std::string& path) {
        size_t size = shm::data_offset + 2 * static_cast<size_t>(capacity_);
#ifdef _WIN32
        file_ = ::CreateFileW(shm::wide(path).c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        if (file_ == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER end;
        end.QuadPart = static_cast<LONGLONG>(size);
        if (!::SetFilePointerEx(file_, end, nullptr, FILE_BEGIN) || !::SetEndOfFile(file_)) return false;
        mapping_ = ::CreateFileMappingW(file_, nullptr, PAGE_READWRITE, 0, 0, nullptr);
        if (!mapping_) return false;
        base_ = static_cast<uint8_t*>(::MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, size));
#else
        fd_ = ::open(path.c_str(), O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0666);
        if (fd_ < 0) return false;
        path_ = path;
        if (::ftruncate(fd_, static_cast<off_t>(size)) != 0) return false;
        void* mapped = ::mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd_, 0);
        base_ = mapped == MAP_FAILED ? nullptr : static_cast<uint8_t*>(mapped);
#endif
        map_size_ = size;
        if (!base_) return false;
        u32(base_ + shm::off_magic).store(shm::magic);
        u32(base_ + shm::off_version).store(shm::version);
        u32(base_ + shm::off_capacity).store(capacity_);
        u32(base_ + shm::off_pid_a).store(shm::current_pid());
        return true;
    }

    void unmap() {
#ifdef _WIN32
        if (base_) ::UnmapViewOfFile(base_);
        if (mapping_) ::CloseHandle(mapping_);
        if (file_ != INVALID_HANDLE_VALUE) ::CloseHandle(file_);   // delete-on-close removes the file once the server lets go too
        mapping_ = nullptr;
        file_ = INVALID_HANDLE_VALUE;
#else
        if (base_) ::munmap(base_, map_size_);
        if (fd_ >= 0) ::close(fd_);
        if (!path_.empty()) ::unlink(path_.c_str());
        fd_ = -1;
#endif
        base_ = nullptr;
    }

    void or_flags(uint32_t bits) { u32(base_ + shm::off_flags).fetch_or(bits); }
    bool attached() const { return (u32(base_ + shm::off_flags).load() & shm::flag_attached_b) != 0; }

    bool peer_gone() {
        uint32_t flags = u32(base_ + shm::off_flags).load();
        if (flags & shm::flag_closed_b) return true;
        if (peer_dead_.load()) return true;
        auto now = std::chrono::steady_clock::now();
        std::lock_guard<std::mutex> lock(check_mutex_);   // the sender and the receiver both look
        if (now - last_check_ >= std::chrono::milliseconds(500)) {
            last_check_ = now;
            uint32_t pid = u32(base_ + shm::off_pid_b).load();
            if (pid != 0 && !shm::process_alive(pid)) peer_dead_ = true;
        }
        return peer_dead_.load();
    }

    uint64_t used(int ring) const {
        uint8_t* c = control(ring);
        return u64(c + shm::off_head).load(std::memory_order_acquire) - u64(c + shm::off_tail).load(std::memory_order_acquire);
    }
    size_t readable() const { return static_cast<size_t>(used(in_ring())); }
    size_t writable() const { return capacity_ - static_cast<size_t>(used(out_ring())); }

    size_t try_read(uint8_t* out, size_t want) {
        uint8_t* c = control(in_ring());
        uint64_t tail = u64(c + shm::off_tail).load(std::memory_order_relaxed);
        uint64_t head = u64(c + shm::off_head).load(std::memory_order_acquire);
        size_t count = static_cast<size_t>(head - tail);
        if (count > want) count = want;
        if (count == 0) return 0;
        uint8_t* d = data(in_ring());
        size_t start = static_cast<size_t>(tail & (capacity_ - 1));
        size_t first = capacity_ - start < count ? capacity_ - start : count;
        std::memcpy(out, d + start, first);
        if (first < count) std::memcpy(out + first, d, count - first);
        u64(c + shm::off_tail).store(tail + count, std::memory_order_release);
        std::atomic_thread_fence(std::memory_order_seq_cst);
        if (u32(c + shm::off_writer_waiting).load() != 0) bell(in_ring() * 2 + 1)->set();
        return count;
    }

    size_t try_write(const uint8_t* in, size_t want) {
        uint8_t* c = control(out_ring());
        uint64_t head = u64(c + shm::off_head).load(std::memory_order_relaxed);
        uint64_t tail = u64(c + shm::off_tail).load(std::memory_order_acquire);
        size_t room = capacity_ - static_cast<size_t>(head - tail);
        size_t count = room < want ? room : want;
        if (count == 0) return 0;
        uint8_t* d = data(out_ring());
        size_t start = static_cast<size_t>(head & (capacity_ - 1));
        size_t first = capacity_ - start < count ? capacity_ - start : count;
        std::memcpy(d + start, in, first);
        if (first < count) std::memcpy(d, in + first, count - first);
        u64(c + shm::off_head).store(head + count, std::memory_order_release);
        std::atomic_thread_fence(std::memory_order_seq_cst);
        if (u32(c + shm::off_reader_waiting).load() != 0) bell(out_ring() * 2)->set();
        return count;
    }

    // "I am about to sleep": announced first, so the other side sees it after it publishes; the ring is looked at again after.
    void announce(bool reader) {
        u32(control(reader ? in_ring() : out_ring()) + (reader ? shm::off_reader_waiting : shm::off_writer_waiting)).store(1);
        std::atomic_thread_fence(std::memory_order_seq_cst);
    }
    void withdraw(bool reader) { u32(control(reader ? in_ring() : out_ring()) + (reader ? shm::off_reader_waiting : shm::off_writer_waiting)).store(0); }

    std::string id_;
    uint32_t capacity_;
    uint8_t* base_ = nullptr;
    size_t map_size_ = 0;
    std::unique_ptr<shm::Doorbell> bells_[4];
    std::atomic<bool> closing_{false};
    std::chrono::steady_clock::time_point last_check_ = std::chrono::steady_clock::now();
    std::atomic<bool> peer_dead_{false};
    std::mutex check_mutex_;
#ifdef _WIN32
    HANDLE file_ = INVALID_HANDLE_VALUE;
    HANDLE mapping_ = nullptr;
#else
    int fd_ = -1;
    std::string path_;
#endif
};

}  // namespace evn
