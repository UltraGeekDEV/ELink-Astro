// EVent for C++: a typed leaf node for desktop systems (threads, sockets).
//
// A leaf has one link, to a router: an EVent node (typically the local C# node) that is the interconnect between
// languages and machines. The leaf is a full NOTES participant for its own endpoints: it takes part in type agreement,
// knows the network's agreed types, and checks every event and call against them. It doesn't forward anything.
// Its own hooks get its own fires and its own functions answer its own calls without a round trip (local loopback).
//
//     evn::Leaf leaf("sensor");
//     if (!leaf.connect_tcp("127.0.0.1", 5698)) return 1;
//     leaf.hook<Reading>("Home.Reading", [](const Reading& r) { ... }, "a room temperature reading");
//     leaf.provide<evn::Void, int32_t>("Home.Count", [](const evn::Void&) { return 42; });
//     leaf.fire("Home.Reading", Reading{21.5f, "den"});                   // blocks until every subscriber handled it
//     leaf.publish("Home.Reading", Reading{21.5f, "den"});                // fire and forget: no acknowledgements, in order, much faster for streams
//     auto counts = leaf.call<int32_t>("Home.Count", evn::Void{});        // std::optional<std::vector<int32_t>>
//     auto later = leaf.call_async<int32_t>("Home.Count", evn::Void{});   // std::future of the same
//
// Handlers run on the leaf's handler threads (Dispatch::Threads), or on the application's own thread when it calls
// poll() (Dispatch::Poll).
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <future>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <random>
#include <set>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "dyn.hpp"
#include "non.hpp"
#include "auth.hpp"
#include "notes.hpp"
#include "shm.hpp"
#include "transport.hpp"
#include "wire.hpp"

namespace evn {

using Millis = std::chrono::milliseconds;
using Bytes = std::vector<uint8_t>;

/// Identifies one hook or provided function, for remove(). False if it couldn't be added (a type conflict).
struct Token {
    uint64_t id = 0;
    explicit operator bool() const { return id != 0; }
};

/// Cancels calls: cancel() ends every call made with this token now, as if its timeout had passed (it completes with
/// the answers that arrived). Copies share the token.
class Cancel {
public:
    Cancel() : s_(std::make_shared<State>()) {}
    void cancel() {
        std::vector<std::function<void()>> hooks;
        {
            std::lock_guard<std::mutex> lock(s_->m);
            if (s_->cancelled) return;
            s_->cancelled = true;
            hooks.swap(s_->hooks);
        }
        for (auto& h : hooks) h();
    }
    bool cancelled() const {
        std::lock_guard<std::mutex> lock(s_->m);
        return s_->cancelled;
    }

private:
    friend class Leaf;
    struct State {
        std::mutex m;
        bool cancelled = false;
        std::vector<std::function<void()>> hooks;
    };
    std::shared_ptr<State> s_;

    // Runs `hook` when cancelled (now, if it is already).
    void on_cancel(const std::function<void()>& hook) const {
        {
            std::lock_guard<std::mutex> lock(s_->m);
            if (!s_->cancelled) {
                s_->hooks.push_back(hook);
                return;
            }
        }
        hook();
    }
};

/// Per call: how long to wait for answers (default: LeafOptions::call_timeout), and a cancel token.
struct CallOptions {
    std::optional<Millis> timeout;
    std::optional<Cancel> cancel;
    CallOptions() = default;
    CallOptions(Millis t) : timeout(t) {}
    CallOptions(Cancel c) : cancel(std::move(c)) {}
    CallOptions(Millis t, Cancel c) : timeout(t), cancel(std::move(c)) {}
};

/// Where handlers and completion callbacks run.
enum class Dispatch {
    Threads,  // on the leaf's handler threads, several at once
    Poll,     // on the application's thread, inside poll() (a GUI, game or event loop)
};

struct LeafOptions {
    /// How long a fire or call waits for the network's answers (then it completes with what arrived).
    Millis call_timeout{30000};
    /// How long connecting waits for the router's handshake.
    Millis handshake_timeout{5000};
    /// How long an accepted but unfinished proposal blocks competing ones.
    Millis proposal_ttl{5000};
    /// Threads for handlers and the leaf's own protocol work: created on demand, at most this many.
    size_t max_workers = 64;
    /// connect_tcp only: dial the router again when the link drops, every reconnect_delay.
    bool reconnect = true;
    Millis reconnect_delay{500};
    Dispatch dispatch = Dispatch::Threads;
    /// Dispatch::Poll: called (on any thread) whenever work is waiting for poll(), to wake the application's loop.
    std::function<void()> wakeup;
    /// Diagnostics (link changes, refused types); none by default.
    std::function<void(const std::string&)> log;
    /// A user ID and the secret it shares with the router, to answer a router that requires authentication: the leaf proves it knows the
    /// secret (the secret is never sent), and requires the router to prove it too. Without it, such a router is refused. Empty: none.
    std::string user_id;
    std::string secret;
    /// With credentials: still join a router that does not ask for authentication (default: refuse it, so a router that skips the challenge,
    /// or something in between, is not trusted).
    bool allow_unauthenticated_router = false;
    /// Keepalive: after keepalive_interval of quiet the router is pinged (anything it sends counts as hearing from it), and after
    /// keepalive_timeout of silence (default three intervals) the link is dropped as if the connection had closed, and dialed again if
    /// reconnect is on. Zero (the default) is off. Only a router that announced protocol 2 or later is pinged. Pings are always answered.
    Millis keepalive_interval{0};
    Millis keepalive_timeout{0};
};

/// One endpoint of the network (see Leaf::list): its ID, "event", "function" or "untyped" (raw events only), and
/// its agreed type (empty when untyped).
struct Endpoint {
    std::string id;
    std::string kind;
    wire::Descriptor type;
};

/// A provider's way to answer later (provide_async): call it once with the answer, or none() for no answer.
template <class Out>
class Answer {
public:
    void operator()(const Out& value) const {
        Bytes b;
        if (encode(value, b))
            reply_(std::move(b));
        else
            reply_(std::nullopt);
    }
    void none() const { reply_(std::nullopt); }

private:
    friend class Leaf;
    explicit Answer(std::function<void(std::optional<Bytes>)> reply) : reply_(std::move(reply)) {}
    std::function<void(std::optional<Bytes>)> reply_;
};

namespace detail {
// What a namespace's "$Describe" answers (C#: NamespaceDescription and friends), for Leaf::list.
struct NsAnnotation {
    std::string Record, Field, Description, Min, Max, Default;
};
EVN_RECORD_NAMED(NsAnnotation, "NOTESFieldAnnotation", Record, Field, Description, Min, Max, Default)
struct NsEndpoint {
    std::string Id, Kind;
    std::vector<std::string> Input, Returns;
    std::string Description;
    std::vector<NsAnnotation> InputAnnotations, ReturnAnnotations;
};
EVN_RECORD_NAMED(NsEndpoint, "EVentEndpoint", Id, Kind, Input, Returns, Description, InputAnnotations, ReturnAnnotations)
struct NsDescription {
    std::string Namespace;
    std::vector<std::string> Imports, Namespaces;
    std::vector<NsEndpoint> Endpoints;
};
EVN_RECORD_NAMED(NsDescription, "EVentNamespace", Namespace, Imports, Namespaces, Endpoints)
}  // namespace detail

class Leaf {
public:
    explicit Leaf(std::string name, LeafOptions options = {}) : name_(std::move(name)), opt_(std::move(options)) {
        store_.proposal_ttl_ms = opt_.proposal_ttl.count();
        timer_ = std::thread(&Leaf::timer_main, this);
        if (opt_.keepalive_interval.count() > 0) keepalive_ = std::thread(&Leaf::keepalive_main, this);
        register_notes();
    }
    ~Leaf() { stop(); }
    Leaf(const Leaf&) = delete;
    Leaf& operator=(const Leaf&) = delete;

    const std::string& name() const { return name_; }

    // ------------------------------------------------------------------ link

    /// Dials the router over TCP and waits for its handshake. With options.reconnect, a dropped link is dialed again.
    /// While the leaf is re-dialing a lost router, this replaces the target.
    bool connect_tcp(const std::string& host, uint16_t port) {
        stop_redialing();
        auto dial = [host, port, this]() -> std::unique_ptr<Transport> {
            std::string error;
            auto t = TcpTransport::connect(host, port, &error);
            if (!t) log(error);
            return t;
        };
        auto first = dial();
        if (!first) return false;
        {
            std::lock_guard<std::mutex> lock(m_);
            dialer_ = opt_.reconnect ? std::function<std::unique_ptr<Transport>()>(dial) : nullptr;
        }
        return attach(std::move(first));
    }

    /// Dials a node of another process on this machine through shared memory and waits for its handshake. `target` is the server's name, or
    /// `tag:driver.serial`, `name:prefix`, `*` for whichever matches; `root` is the registry folder (default: the one every EVent process uses).
    /// With options.reconnect, a dropped link is dialed again. See shm.hpp.
    bool connect_shm(const std::string& target, const std::string& root = std::string()) {
        stop_redialing();
        auto dial = [target, root, this]() -> std::unique_ptr<Transport> {
            std::string error;
            auto t = ShmTransport::connect(target, root, &error);
            if (!t) log(error);
            return t;
        };
        auto first = dial();
        if (!first) return false;
        {
            std::lock_guard<std::mutex> lock(m_);
            dialer_ = opt_.reconnect ? std::function<std::unique_ptr<Transport>()>(dial) : nullptr;
        }
        return attach(std::move(first));
    }

    /// Uses an already open transport as the link to the router and waits for its handshake. Not re-dialed.
    bool attach(std::unique_ptr<Transport> transport) {
        if (!transport || stopping_) return false;
        uint64_t generation;
        {
            std::unique_lock<std::mutex> lock(m_);
            if (link_ || reader_running_) return false;  // one link at a time
            if (reader_.joinable()) {
                lock.unlock();
                reader_.join();
                lock.lock();
            }
            generation = begin_link(std::shared_ptr<Transport>(std::move(transport)));
            reader_running_ = true;
            reader_ = std::thread(&Leaf::reader_main, this);
        }
        std::unique_lock<std::mutex> lock(m_);
        bool up = link_cv_.wait_for(lock, opt_.handshake_timeout, [&] { return stopping_ || generation_ != generation || up_ || !link_; });
        if (up && up_ && generation_ == generation) return true;
        if (link_ && generation_ == generation) link_->close();
        return false;
    }

    /// True when this leaf proved its user ID to the router and the router proved it knows the secret too (credentials set).
    bool authenticated() const {
        std::lock_guard<std::mutex> lock(m_);
        return authenticated_;
    }

    /// The last measured ping round trip to the router in milliseconds (needs keepalive), or -1 if none yet.
    int64_t round_trip_ms() const {
        std::lock_guard<std::mutex> lock(m_);
        return round_trip_ms_;
    }

    /// The router's name as it gave it in the authentication exchange (empty without it).
    std::string router_realm() const {
        std::lock_guard<std::mutex> lock(m_);
        return server_realm_;
    }

    /// True while the link to the router is up (handshake done).
    bool connected() const {
        std::lock_guard<std::mutex> lock(m_);
        return up_;
    }

    /// What the router said about itself in its handshake (implementation, protocol version); empty before a link.
    wire::Handshake router_handshake() const {
        std::lock_guard<std::mutex> lock(m_);
        return router_;
    }

    /// Called when the link comes up (true) or goes down (false, with the reason); dispatched like a handler.
    void on_link(std::function<void(bool up, const std::string& reason)> handler) {
        std::lock_guard<std::mutex> lock(m_);
        on_link_ = std::move(handler);
    }

    /// Leaves the network: closes the link, completes waiting fires and calls, stops the leaf's threads.
    /// Don't call it from a handler.
    void stop() {
        std::vector<std::shared_ptr<CallState>> calls;
        std::vector<std::function<void(bool)>> waiting;
        {
            std::lock_guard<std::mutex> lock(m_);
            if (stopping_) return;
            stopping_ = true;
            dialer_ = nullptr;
            if (link_) link_->close();
            for (auto& c : active_) calls.push_back(c.second);
        }
        {
            std::lock_guard<std::mutex> lock(gate_m_);
            waiting.swap(gate_waiters_);
        }
        for (auto& c : calls) complete(c, true);
        for (auto& w : waiting) w(false);
        link_cv_.notify_all();
        gate_cv_.notify_all();
        user_cv_.notify_all();
        {
            std::lock_guard<std::mutex> lock(timer_m_);
        }
        timer_cv_.notify_all();
        {
            std::lock_guard<std::mutex> lock(ka_m_);
        }
        ka_cv_.notify_all();
        if (keepalive_.joinable()) keepalive_.join();
        if (reader_.joinable()) reader_.join();
        if (timer_.joinable()) timer_.join();
        {
            std::lock_guard<std::mutex> lock(work_m_);
        }
        work_cv_.notify_all();
        std::vector<std::thread> workers;
        {
            std::lock_guard<std::mutex> lock(work_m_);
            workers.swap(workers_);
        }
        for (auto& w : workers) {
            if (w.get_id() == std::this_thread::get_id())
                w.detach();
            else
                w.join();
        }
    }

    // ------------------------------------------------------------------ Dispatch::Poll

    /// Runs the handlers and callbacks waiting for this thread (Dispatch::Poll); how many ran.
    size_t poll(size_t max = static_cast<size_t>(-1)) {
        size_t ran = 0;
        while (ran < max) {
            std::function<void()> job;
            {
                std::lock_guard<std::mutex> lock(user_m_);
                if (user_jobs_.empty()) break;
                job = std::move(user_jobs_.front());
                user_jobs_.pop_front();
            }
            job();
            ran++;
        }
        return ran;
    }

    /// Waits until work is waiting for poll() or the timeout passes; true if there is work.
    bool wait(Millis timeout) {
        std::unique_lock<std::mutex> lock(user_m_);
        user_cv_.wait_for(lock, timeout, [&] { return !user_jobs_.empty() || stopping_; });
        return !user_jobs_.empty();
    }

    // ------------------------------------------------------------------ typed endpoints

    /// Subscribes to typed event `id` (agreeing T as its type with the network first, if nobody has).
    /// callback: void(const T&). Empty token on a type conflict.
    template <class T, class F>
    Token hook(const std::string& id, F callback, const std::string& description = "") {
        const Signature& in = signature_of<T>();
        if (!usable(id, in) || !agree(id, in, signature_of<Void>(), description)) return {};
        return add_call_handler(id, sync_handler([callback](const Bytes& input, Bytes&) {
                                    T value{};
                                    if (decode(input, value)) callback(static_cast<const T&>(value));
                                    return false;  // an event's subscribers answer with nothing
                                }));
    }

    /// Provides typed function `id`: In -> Out (agreed with the network first, if nobody has). handler: Out(const In&),
    /// or std::optional<Out>(const In&) where nullopt contributes no answer. Empty token on a type conflict.
    template <class In, class Out, class F>
    Token provide(const std::string& id, F handler, const std::string& description = "") {
        const Signature& in = signature_of<In>();
        const Signature& out = signature_of<Out>();
        if (!usable(id, in) || !usable(id, out) || !agree(id, in, out, description)) return {};
        return add_call_handler(id, sync_handler([handler](const Bytes& input, Bytes& answer) {
                                    In value{};
                                    if (!decode(input, value)) return false;
                                    auto result = handler(static_cast<const In&>(value));
                                    if constexpr (is_optional_answer<decltype(result)>::value) {
                                        return result.has_value() && encode(static_cast<const Out&>(*result), answer);
                                    } else {
                                        return encode(static_cast<const Out&>(result), answer);
                                    }
                                }));
    }

    /// provide() for a handler that answers later: handler(const In&, evn::Answer<Out> answer), and answer(value) (or
    /// answer.none()) exactly once, from any thread, before the leaf stops. The caller waits for it (up to its timeout).
    template <class In, class Out, class F>
    Token provide_async(const std::string& id, F handler, const std::string& description = "") {
        const Signature& in = signature_of<In>();
        const Signature& out = signature_of<Out>();
        if (!usable(id, in) || !usable(id, out) || !agree(id, in, out, description)) return {};
        return add_call_handler(id, [handler](const Bytes& input, Reply reply) {
            In value{};
            if (!decode(input, value)) {
                reply(std::nullopt);
                return;
            }
            handler(static_cast<const In&>(value), Answer<Out>(reply));
        });
    }

    /// Removes a hook or provided function. The network stops routing its ID here when nothing else here handles it.
    /// The agreed type stays (it belongs to the network).
    bool remove(Token token) {
        std::lock_guard<std::mutex> lock(m_);
        auto found = token_ids_.find(token.id);
        if (found == token_ids_.end()) return false;
        std::string id = found->second;
        token_ids_.erase(found);
        auto& list = local_[id];
        for (auto it = list.begin(); it != list.end(); ++it) {
            if (it->token == token.id) {
                list.erase(it);
                break;
            }
        }
        if (list.empty()) {
            local_.erase(id);
            send_admin_locked(wire::ids::EventRemoved, id);
        }
        return true;
    }

    /// Delivers `value` to every subscriber of `id` (here and in the network) and waits for them. True also when
    /// nobody subscribed; false on a type mismatch (T isn't the agreed type, or `id` is a function) or when stopped.
    template <class T>
    bool fire(const std::string& id, const T& value, CallOptions options = {}) {
        Gate gate(*this);
        if (!gate) return false;
        auto agreed = find_agreed(id);
        if (agreed.first == Check::Nobody) return true;
        Bytes bytes;
        if (!checked(id, agreed, signature_of<T>(), signature_of<Void>()) || !encode(value, bytes)) return false;
        call_blocking(id, std::move(bytes), options);
        return true;
    }

    /// Fire and forget: hands `value` to every subscriber (here and in the network) and returns without waiting for any of them;
    /// nothing is kept for it and no acknowledgement travels, so streams of readings move several times faster than with fire().
    /// Events from this leaf reach a subscriber in the order published (a subscriber's handlers get one ID's events one at a
    /// time). It says nothing about the event having been handled: a router or subscriber that is gone, a link that breaks or a
    /// handler that throws loses it silently. True also when nobody subscribed; false on a type mismatch or when stopped.
    template <class T>
    bool publish(const std::string& id, const T& value) {
        Gate gate(*this);
        if (!gate) return false;
        auto agreed = find_agreed(id);
        if (agreed.first == Check::Nobody) return true;
        Bytes bytes;
        if (!checked(id, agreed, signature_of<T>(), signature_of<Void>()) || !encode(value, bytes)) return false;
        publish_bytes(id, std::move(bytes));
        return true;
    }

    /// Calls typed function `id` on every provider (here and in the network): one entry per answer. Empty if there
    /// are no providers; nullopt on a type mismatch or when stopped. Providers that don't answer within the timeout
    /// (or before a cancel) are left out.
    template <class Out, class In>
    std::optional<std::vector<Out>> call(const std::string& id, const In& input, CallOptions options = {}) {
        Gate gate(*this);
        if (!gate) return std::nullopt;
        auto agreed = find_agreed(id);
        if (agreed.first == Check::Nobody) return std::vector<Out>{};
        Bytes bytes;
        if (!checked(id, agreed, signature_of<In>(), signature_of<Out>()) || !encode(input, bytes)) return std::nullopt;
        return decode_answers<Out>(call_blocking(id, std::move(bytes), options));
    }

    /// fire() without waiting: the future completes when every subscriber handled it. Dropping the future is
    /// fire-and-forget. This leaf's own subscribers run on its handler threads (or in poll()).
    template <class T>
    std::future<bool> fire_async(const std::string& id, const T& value, CallOptions options = {}) {
        auto result = std::make_shared<std::promise<bool>>();
        auto future = result->get_future();
        typed_async<T, Void>(id, value, options, [result](Status s, std::vector<Bytes>) { result->set_value(s == Status::Ok || s == Status::Nobody); });
        return future;
    }

    /// fire_async() with a callback, done(bool), dispatched like a handler.
    template <class T, class F>
    void fire_then(const std::string& id, const T& value, F done, CallOptions options = {}) {
        typed_async<T, Void>(id, value, options, [this, done](Status s, std::vector<Bytes>) {
            bool ok = s == Status::Ok || s == Status::Nobody;
            dispatch([done, ok] { done(ok); });
        });
    }

    /// call() without waiting.
    template <class Out, class In>
    std::future<std::optional<std::vector<Out>>> call_async(const std::string& id, const In& input, CallOptions options = {}) {
        auto result = std::make_shared<std::promise<std::optional<std::vector<Out>>>>();
        auto future = result->get_future();
        typed_async<In, Out>(id, input, options, [result](Status s, std::vector<Bytes> answers) { result->set_value(async_answers<Out>(s, answers)); });
        return future;
    }

    /// call_async() with a callback, done(std::optional<std::vector<Out>>), dispatched like a handler.
    template <class Out, class In, class F>
    void call_then(const std::string& id, const In& input, F done, CallOptions options = {}) {
        typed_async<In, Out>(id, input, options, [this, done](Status s, std::vector<Bytes> answers) {
            auto result = async_answers<Out>(s, answers);
            dispatch([done, result] { done(result); });
        });
    }

    // ------------------------------------------------------------------ raw events

    /// Subscribes to raw (untyped) event `id`: callback(const std::vector<uint8_t>& payload). No type checks; don't mix
    /// raw and typed use of one ID.
    template <class F>
    Token hook_raw(const std::string& id, F callback) {
        Entry e;
        e.raw = [callback](const Bytes& data) { callback(data); };
        return add_entry(id, std::move(e));
    }

    /// Sends raw event `id` to its subscribers (here and in the network), without waiting.
    void fire_raw(const std::string& id, const Bytes& data) {
        std::vector<Entry> mine;
        bool remote;
        {
            std::lock_guard<std::mutex> lock(m_);
            remote = up_ && routed_remote(id);
            mine = entries_locked(id, false);
        }
        if (remote) send({id, wire::PackageType::Data, data});
        auto shared = std::make_shared<const Bytes>(data);
        for (auto& e : mine) dispatch([this, e, shared] { invoke_raw(e, *shared); });
    }

    // ------------------------------------------------------------------ dynamic types

    /// Subscribes with a dynamic value (dyn::Value) instead of a struct. With `propose` empty the network must have a
    /// type for `id` already; otherwise `propose` (a signature) is agreed first if nobody has one.
    Token hook_dynamic(const std::string& id, std::function<void(const dyn::Value&)> callback, const std::vector<std::string>& propose = {},
                       const std::string& description = "") {
        auto schemas = dynamic_schemas(id, true, propose, {}, description);
        if (!schemas) return {};
        auto in = schemas->first;
        return add_call_handler(id, sync_handler([in, callback](const Bytes& input, Bytes&) {
                                    dyn::Value v;
                                    if (dyn::decode(input, in->root(), v)) callback(v);
                                    return false;
                                }));
    }

    /// Provides `id` with dynamic values: handler(const dyn::Value&) -> std::optional<dyn::Value> (nullopt: no answer).
    Token provide_dynamic(const std::string& id, std::function<std::optional<dyn::Value>(const dyn::Value&)> handler,
                          const std::vector<std::string>& propose_input = {}, const std::vector<std::string>& propose_output = {},
                          const std::string& description = "") {
        auto schemas = dynamic_schemas(id, false, propose_input, propose_output, description);
        if (!schemas) return {};
        auto in = schemas->first, out = schemas->second;
        return add_call_handler(id, sync_handler([in, out, handler](const Bytes& input, Bytes& answer) {
                                    dyn::Value v;
                                    if (!dyn::decode(input, in->root(), v)) return false;
                                    auto result = handler(v);
                                    return result && dyn::encode(*result, out->root(), answer);
                                }));
    }

    /// fire() with a dynamic value, checked against the agreed type.
    bool fire_dynamic(const std::string& id, const dyn::Value& value, CallOptions options = {}) {
        Gate gate(*this);
        if (!gate) return false;
        auto agreed = find_agreed(id);
        if (agreed.first == Check::Nobody) return true;
        if (agreed.first == Check::Mismatch || !agreed.second.is_event()) return false;
        auto schema = schema_for(agreed.second.expected);
        Bytes bytes;
        std::string why;
        if (!schema || !dyn::encode(value, schema->root(), bytes, &why)) {
            log("fire " + id + ": the value doesn't fit the agreed type: " + why);
            return false;
        }
        call_blocking(id, std::move(bytes), options);
        return true;
    }

    /// call() with dynamic values, checked against the agreed type.
    std::optional<std::vector<dyn::Value>> call_dynamic(const std::string& id, const dyn::Value& input, CallOptions options = {}) {
        Gate gate(*this);
        if (!gate) return std::nullopt;
        auto agreed = find_agreed(id);
        if (agreed.first == Check::Nobody) return std::vector<dyn::Value>{};
        if (agreed.first == Check::Mismatch || agreed.second.is_event()) return std::nullopt;
        auto in = schema_for(agreed.second.expected), out = schema_for(agreed.second.returns);
        Bytes bytes;
        std::string why;
        if (!in || !out || !dyn::encode(input, in->root(), bytes, &why)) {
            log("call " + id + ": the value doesn't fit the agreed type: " + why);
            return std::nullopt;
        }
        std::vector<dyn::Value> answers;
        for (const auto& a : call_blocking(id, std::move(bytes), options)) {
            dyn::Value v;
            if (dyn::decode(a, out->root(), v)) answers.push_back(std::move(v));
        }
        return answers;
    }

    // ------------------------------------------------------------------ discovery

    /// The agreed types this leaf knows.
    std::vector<wire::Descriptor> known_types() const {
        std::lock_guard<std::mutex> lock(types_m_);
        return store_.all();
    }

    /// The agreed type of `id`: known here, or asked from the network. nullopt if nobody agreed one.
    std::optional<wire::Descriptor> type_of(const std::string& id) {
        auto agreed = find_agreed(id);
        if (agreed.first != Check::Ok) return std::nullopt;
        return agreed.second;
    }

    /// Every endpoint of the network, sorted by ID, like C#'s NetworkDirectory: IDs routed by name (typed or not),
    /// and each namespace (a prefix route X.*) expanded through its $Describe, nested ones included. The protocol's
    /// own IDs and IDs with a '$' part are left out.
    std::vector<Endpoint> list(CallOptions options = {}) {
        std::set<std::string> routed;
        {
            std::lock_guard<std::mutex> lock(m_);
            for (const auto& id : remote_) routed.insert(id);
            for (const auto& e : local_) routed.insert(e.first);
        }
        std::vector<std::string> by_name, prefixes;
        for (const auto& id : routed) {
            if (id.size() > 2 && id.compare(id.size() - 2, 2, ".*") == 0) {
                std::string prefix = id.substr(0, id.size() - 2);
                if (application_id(prefix)) prefixes.push_back(prefix);
            } else if (application_id(id)) {
                by_name.push_back(id);
            }
        }
        std::vector<std::string> unknown;
        {
            std::lock_guard<std::mutex> lock(types_m_);
            for (const auto& id : by_name) {
                if (!store_.find(id)) unknown.push_back(id);
            }
        }
        if (!unknown.empty()) {
            for (const auto& a : call_blocking(wire::ids::QueryDescriptorSet, wire::strings_payload(unknown), options)) {
                Reader r(a);
                std::vector<wire::Descriptor> found;
                if (!wire::read_descriptors(r, found)) continue;
                std::lock_guard<std::mutex> lock(types_m_);
                for (const auto& d : found) store_.adopt(d);
            }
        }
        std::map<std::string, Endpoint> result;
        {
            std::lock_guard<std::mutex> lock(types_m_);
            for (const auto& id : by_name) {
                auto d = store_.find(id);
                result[id] = d ? Endpoint{id, d->is_event() ? "event" : "function", *d} : Endpoint{id, "untyped", {}};
            }
        }
        for (const auto& prefix : prefixes) {
            if (describe_tree(prefix, 16, options, result)) continue;
            // A prefix route that isn't a namespace (an import of plain IDs): the agreed types known under it.
            std::lock_guard<std::mutex> lock(types_m_);
            for (const auto& d : store_.all()) {
                if (d.id.compare(0, prefix.size() + 1, prefix + ".") == 0 && application_id(d.id)) {
                    result.emplace(d.id, Endpoint{d.id, d.is_event() ? "event" : "function", d});
                }
            }
        }
        std::vector<Endpoint> endpoints;
        for (auto& e : result) endpoints.push_back(std::move(e.second));
        return endpoints;
    }

private:
    using Reply = std::function<void(std::optional<Bytes>)>;
    using Handler = std::function<void(const Bytes& input, Reply reply)>;
    struct Entry {
        uint64_t token = 0;
        Handler call;                              // a typed handler (events and functions)
        std::function<void(const Bytes&)> raw;     // a raw event subscriber
        bool internal = false;                     // the leaf's own NOTES responders: never dispatched to poll()
    };
    using Clock = std::chrono::steady_clock;
    struct CallState {
        uint64_t id = 0;
        bool remote = false;  // the router's answer is outstanding
        int locals = 0;       // this leaf's handlers that haven't answered
        std::vector<Bytes> answers;
        std::atomic<bool> finished{false};
        std::function<void(std::vector<Bytes>)> done;
        bool armed = false;  // under timer_m_
        std::multimap<Clock::time_point, std::weak_ptr<CallState>>::iterator timer;
    };
    enum class Check { Ok, Nobody, Mismatch };
    enum class Status { Ok, Nobody, Mismatch, Stopped };

    template <class T>
    struct is_optional_answer : std::false_type {};
    template <class T>
    struct is_optional_answer<std::optional<T>> : std::true_type {};

    std::string name_;
    LeafOptions opt_;

    // m_ guards the link, the interest sets and the calls in flight.
    mutable std::mutex m_;
    std::map<std::string, std::vector<Entry>> local_;  // this leaf's handlers (its interest)
    std::map<uint64_t, std::string> token_ids_;
    std::set<std::string> remote_;                     // what the router routes onward (its interest)
    std::shared_ptr<Transport> link_;
    bool up_ = false;
    uint64_t generation_ = 0;
    bool reader_running_ = false;
    std::thread reader_;
    std::condition_variable link_cv_;
    std::function<std::unique_ptr<Transport>()> dialer_;
    std::function<void(bool, const std::string&)> on_link_;
    wire::Handshake router_;
    std::map<uint64_t, std::shared_ptr<CallState>> active_;
    // Authentication (under m_): with credentials, nothing is said about interest until the router has proved it knows the secret.
    // Keepalive: when anything last arrived (milliseconds on the steady clock), the ping waiting for its pong, the last round trip.
    std::atomic<int64_t> last_heard_ms_{0};
    int64_t last_ping_ms_ = 0;       // under m_
    uint64_t ping_token_ = 0;        // under m_: the ping waiting for its pong (0: none)
    int64_t ping_sent_ms_ = 0;
    int64_t round_trip_ms_ = -1;
    std::string close_reason_;       // under m_: why this leaf closed the link itself (keepalive), for the link-down message
    std::mutex ka_m_;
    std::condition_variable ka_cv_;
    std::thread keepalive_;
    bool auth_done_ = true;
    bool authenticated_ = false;
    std::vector<uint8_t> expected_server_proof_;
    std::string server_realm_;
    uint64_t next_call_ = 1;
    uint64_t next_token_ = 1;
    std::atomic<bool> stopping_{false};
    std::mutex send_m_;

    mutable std::mutex types_m_;
    TypeStore store_;
    std::map<std::string, std::shared_ptr<const dyn::Schema>> schemas_;
    std::mt19937 random_{std::random_device{}()};

    // The gate: fires, calls and agreements run inside it; a network merge closes it (StopEvents) and waits for them.
    std::mutex gate_m_;
    std::condition_variable gate_cv_;
    bool gate_closed_ = false;
    int inside_ = 0;
    std::vector<std::function<void(bool)>> gate_waiters_;  // async operations waiting for the gate to open

    std::mutex timer_m_;
    std::condition_variable timer_cv_;
    std::multimap<Clock::time_point, std::weak_ptr<CallState>> timers_;
    std::thread timer_;

    std::mutex work_m_;
    std::condition_variable work_cv_;
    std::deque<std::function<void()>> jobs_;
    std::mutex lane_m_;
    std::map<std::string, std::deque<std::function<void()>>> lanes_;
    std::vector<std::thread> workers_;
    size_t idle_ = 0;

    std::mutex user_m_;
    std::condition_variable user_cv_;
    std::deque<std::function<void()>> user_jobs_;

    void log(const std::string& text) const {
        if (opt_.log) opt_.log(name_ + ": " + text);
    }

    bool usable(const std::string& id, const Signature& sig) const {
        if (sig.ok()) return true;
        log(id + ": not a NOTES type: " + sig.error);
        return false;
    }

    static bool matches(const wire::Descriptor& d, const Signature& in, const Signature& out) {
        return in.ok() && out.ok() && d.expected == in.entries && d.returns == out.entries;
    }

    static std::string join(const std::vector<std::string>& v) {
        std::string s;
        for (const auto& e : v) s += (s.empty() ? "" : " ") + e;
        return s;
    }

    bool checked(const std::string& id, const std::pair<Check, wire::Descriptor>& agreed, const Signature& in, const Signature& out) const {
        if (agreed.first == Check::Ok && matches(agreed.second, in, out)) return true;
        log(id + ": " + (agreed.first == Check::Mismatch ? std::string("in use without an agreed type")
                                                          : "the agreed type is " + join(agreed.second.expected) + " -> " + join(agreed.second.returns) + ", not " +
                                                                join(in.entries) + " -> " + join(out.entries)));
        return false;
    }

    template <class Out>
    static std::vector<Out> decode_answers(const std::vector<Bytes>& answers) {
        std::vector<Out> values;
        for (const auto& a : answers) {
            Out v{};
            if (decode(a, v)) values.push_back(std::move(v));  // an unreadable answer is left out
        }
        return values;
    }

    template <class Out>
    static std::optional<std::vector<Out>> async_answers(Status s, const std::vector<Bytes>& answers) {
        if (s == Status::Nobody) return std::vector<Out>{};
        if (s != Status::Ok) return std::nullopt;
        return decode_answers<Out>(answers);
    }

    static bool application_id(const std::string& id) {
        static const std::set<std::string> protocol = {
            wire::ids::InterconnectRunning, wire::ids::ListEvents, wire::ids::QueryEvents, wire::ids::EventAdded, wire::ids::EventRemoved,
            wire::ids::LinkFault, "CreateInterconnect", "DisconnectInterconnect", wire::ids::TryInitiate, wire::ids::Finalize,
            wire::ids::Abort, wire::ids::QueryDescriptors, wire::ids::QueryDescriptorSet, wire::ids::ShareDescriptors,
            wire::ids::StopEvents, wire::ids::AllowEvents, wire::ids::AwaitReady, "NOTESMergePing", "HandleEvent"};
        if (id.empty() || protocol.count(id)) return false;
        size_t start = 0;
        while (true) {
            size_t dot = id.find('.', start);
            std::string part = id.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
            if (part.empty() || part[0] == '$') return false;
            if (dot == std::string::npos) return true;
            start = dot + 1;
        }
    }

    // ------------------------------------------------------------------ gate

    class Gate {
    public:
        explicit Gate(Leaf& leaf) : leaf_(leaf) {
            std::unique_lock<std::mutex> lock(leaf_.gate_m_);
            leaf_.gate_cv_.wait(lock, [&] { return !leaf_.gate_closed_ || leaf_.stopping_; });
            entered_ = !leaf_.stopping_;
            if (entered_) leaf_.inside_++;
        }
        ~Gate() {
            if (entered_) leaf_.leave_gate();
        }
        explicit operator bool() const { return entered_; }

    private:
        Leaf& leaf_;
        bool entered_ = false;
    };

    // go(true) once inside the gate (now, or when a merge freeze ends), go(false) if the leaf stops first.
    void enter_gate_async(std::function<void(bool)> go) {
        {
            std::unique_lock<std::mutex> lock(gate_m_);
            if (!stopping_ && gate_closed_) {
                gate_waiters_.push_back(std::move(go));
                return;
            }
            if (!stopping_) inside_++;
        }
        go(!stopping_);
    }

    void leave_gate() {
        std::lock_guard<std::mutex> lock(gate_m_);
        inside_--;
        gate_cv_.notify_all();
    }

    // ------------------------------------------------------------------ handlers and interest

    static Handler sync_handler(std::function<bool(const Bytes&, Bytes&)> fn) {
        return [fn](const Bytes& input, Reply reply) {
            Bytes answer;
            if (fn(input, answer))
                reply(std::move(answer));
            else
                reply(std::nullopt);
        };
    }

    Token add_call_handler(const std::string& id, Handler h, bool internal = false) {
        Entry e;
        e.call = std::move(h);
        e.internal = internal;
        return add_entry(id, std::move(e));
    }

    Token add_entry(const std::string& id, Entry e) {
        Token token;
        bool announced, up;
        {
            std::lock_guard<std::mutex> lock(m_);
            e.token = next_token_++;
            auto& list = local_[id];
            bool internal = e.internal;
            list.push_back(std::move(e));
            token_ids_[list.back().token] = id;
            token = {list.back().token};
            announced = list.size() == 1 && !internal;
            if (list.size() == 1) send_admin_locked(wire::ids::EventAdded, id);
            up = up_;
        }
        // The EventAdded notice travels ahead of this call on the link, and every node answers AwaitReady only after
        // handling what came before it: once it completes, the whole network routes `id` here (as a .NET node does).
        if (announced && up && !stopping_) call_blocking(wire::ids::AwaitReady, {0}, CallOptions(Millis(5000)));
        return token;
    }

    // Under m_: this leaf's handlers of `id`, typed (calls) or raw.
    std::vector<Entry> entries_locked(const std::string& id, bool calls) const {
        std::vector<Entry> found;
        auto list = local_.find(id);
        if (list == local_.end()) return found;
        for (const auto& e : list->second) {
            if (calls ? static_cast<bool>(e.call) : static_cast<bool>(e.raw)) found.push_back(e);
        }
        return found;
    }

    // Under m_: an admin notice about `id` (EventAdded/EventRemoved), if there is a link.
    void send_admin_locked(const char* admin, const std::string& id) {
        if (link_) send_to(link_, {admin, wire::PackageType::ServerAdminEvent, wire::string_payload(id)});
    }

    bool send(const wire::Package& p) {
        std::shared_ptr<Transport> link;
        {
            std::lock_guard<std::mutex> lock(m_);
            link = link_;
        }
        return link && send_to(link, p);
    }

    bool send_to(const std::shared_ptr<Transport>& link, const wire::Package& p) {
        Writer w;
        if (!wire::write_package(w, p)) return false;
        std::lock_guard<std::mutex> lock(send_m_);
        return link->send(w.out.data(), w.out.size());
    }

    // Sends a frame that is already built.
    bool send_frame_to(const std::shared_ptr<Transport>& link, const Bytes& frame) {
        if (frame.empty()) return false;
        std::lock_guard<std::mutex> lock(send_m_);
        return link->send(frame.data(), frame.size());
    }

    // Under m_: the router routes `id` onward (by name, or through a prefix route A.* / A.B.*).
    bool routed_remote(const std::string& id) const {
        if (remote_.count(id)) return true;
        for (size_t i = id.find('.'); i != std::string::npos && i + 1 < id.size(); i = id.find('.', i + 1)) {
            if (remote_.count(id.substr(0, i + 1) + "*")) return true;
        }
        return false;
    }

    std::vector<std::string> interest_locked() const {
        std::vector<std::string> ids;
        for (const auto& e : local_) ids.push_back(e.first);
        return ids;
    }

    // Runs a handler; `reply` takes its answer (once). A handler that throws gives no answer.
    void invoke(const Entry& e, const Bytes& input, const Reply& reply) const {
#if defined(__cpp_exceptions) || defined(__EXCEPTIONS)
        try {
            e.call(input, reply);
        } catch (const std::exception& ex) {
            log(std::string("a handler threw: ") + ex.what());
            reply(std::nullopt);
        } catch (...) {
            log("a handler threw");
            reply(std::nullopt);
        }
#else
        e.call(input, reply);
#endif
    }

    void invoke_raw(const Entry& e, const Bytes& data) const {
#if defined(__cpp_exceptions) || defined(__EXCEPTIONS)
        try {
            e.raw(data);
        } catch (...) {
            log("a raw handler threw");
        }
#else
        e.raw(data);
#endif
    }

    // A reply that counts once, however often it is called.
    static Reply once(std::function<void(std::optional<Bytes>)> fn) {
        auto used = std::make_shared<std::atomic<bool>>(false);
        return [used, fn](std::optional<Bytes> a) {
            if (!used->exchange(true)) fn(std::move(a));
        };
    }

    // ------------------------------------------------------------------ calls

    // Calls `id` everywhere it is handled: the router (if it routes it onward) and this leaf's own handlers (run here
    // and now with `inline_locals`, else on handler threads / poll()). `done` gets every answer once all answered, the
    // timeout passed or the call was cancelled; it runs on whichever thread finished the call and must not block.
    void start_call(const std::string& id, std::shared_ptr<const Bytes> input, Millis timeout, const std::optional<Cancel>& cancel, bool inline_locals,
                    std::function<void(std::vector<Bytes>)> done) {
        auto st = std::make_shared<CallState>();
        st->done = std::move(done);
        std::shared_ptr<Transport> link;
        std::vector<Entry> mine;
        {
            std::lock_guard<std::mutex> lock(m_);
            st->id = next_call_++;
            if (stopping_) {
                st->finished = true;
            } else {
                active_[st->id] = st;
                if (up_ && routed_remote(id)) {
                    st->remote = true;
                    link = link_;
                }
                mine = entries_locked(id, true);
                st->locals = static_cast<int>(mine.size());
            }
        }
        if (st->finished) {
            st->done({});
            return;
        }
        if (st->remote || !inline_locals) arm(Clock::now() + timeout, st);
        if (cancel) {
            std::weak_ptr<CallState> weak = st;
            cancel->on_cancel([this, weak] {
                auto s = weak.lock();
                if (s && !s->finished) complete(s, true);
            });
        }
        if (link) {
            if (!send_frame_to(link, wire::call_frame(id, *input, {st->id, 0}))) {
                std::lock_guard<std::mutex> lock(m_);
                st->remote = false;
            }
        }
        for (auto& e : mine) {
            Reply reply = once([this, st](std::optional<Bytes> a) {
                {
                    std::lock_guard<std::mutex> lock(m_);
                    if (a && !a->empty() && !st->finished) st->answers.push_back(std::move(*a));
                    st->locals--;
                }
                complete(st, false);
            });
            if (inline_locals) {
                invoke(e, *input, reply);
            } else {
                auto job = [this, e, input, reply] { invoke(e, *input, reply); };
                if (e.internal)
                    post(std::move(job));
                else
                    dispatch(std::move(job));
            }
        }
        complete(st, false);
    }

    // Finishes a call if everyone answered (or regardless, with `force`): runs its `done` once.
    void complete(const std::shared_ptr<CallState>& st, bool force) {
        std::function<void(std::vector<Bytes>)> done;
        std::vector<Bytes> answers;
        {
            std::lock_guard<std::mutex> lock(m_);
            if (st->finished) return;
            if (!force && (st->remote || st->locals > 0)) return;
            st->finished = true;
            active_.erase(st->id);
            done.swap(st->done);
            answers.swap(st->answers);
        }
        {
            std::lock_guard<std::mutex> lock(timer_m_);
            if (st->armed) {
                timers_.erase(st->timer);
                st->armed = false;
            }
        }
        if (done) done(std::move(answers));
    }

    std::vector<Bytes> call_blocking(const std::string& id, Bytes input, const CallOptions& options) {
        auto result = std::make_shared<std::promise<std::vector<Bytes>>>();
        auto future = result->get_future();
        start_call(id, std::make_shared<const Bytes>(std::move(input)), options.timeout.value_or(opt_.call_timeout), options.cancel, true,
                   [result](std::vector<Bytes> answers) { result->set_value(std::move(answers)); });
        return future.get();
    }

    // The asynchronous path of a typed fire or call: gate, agreed type, then the call.
    template <class In, class Out>
    void typed_async(const std::string& id, const In& value, const CallOptions& options, std::function<void(Status, std::vector<Bytes>)> result) {
        auto bytes = std::make_shared<Bytes>();
        bool encoded = encode(value, *bytes);
        Millis timeout = options.timeout.value_or(opt_.call_timeout);
        auto cancel = options.cancel;
        enter_gate_async([this, id, bytes, encoded, timeout, cancel, result](bool entered) {
            if (!entered) {
                result(Status::Stopped, {});
                return;
            }
            find_agreed_async(id, [this, id, bytes, encoded, timeout, cancel, result](Check c, wire::Descriptor d) {
                if (c == Check::Nobody) {
                    leave_gate();
                    result(Status::Nobody, {});
                    return;
                }
                if (!checked(id, {c, d}, signature_of<In>(), signature_of<Out>()) || !encoded) {
                    leave_gate();
                    result(Status::Mismatch, {});
                    return;
                }
                start_call(id, bytes, timeout, cancel, false, [this, result](std::vector<Bytes> answers) {
                    leave_gate();
                    result(Status::Ok, std::move(answers));
                });
            });
        });
    }

    // ------------------------------------------------------------------ NOTES

    std::pair<Check, wire::Descriptor> find_agreed(const std::string& id) {
        auto result = std::make_shared<std::promise<std::pair<Check, wire::Descriptor>>>();
        auto future = result->get_future();
        find_agreed_async(id, [result](Check c, wire::Descriptor d) { result->set_value({c, std::move(d)}); }, true);
        return future.get();
    }

    // The agreed type of `id`: known here, or asked from the network and adopted. Nobody interested: Nobody; routed by
    // name without an agreed type: Mismatch.
    void find_agreed_async(const std::string& id, std::function<void(Check, wire::Descriptor)> done, bool inline_locals = false) {
        {
            std::unique_lock<std::mutex> lock(types_m_);
            if (auto d = store_.find(id)) {
                auto copy = *d;
                lock.unlock();
                done(Check::Ok, std::move(copy));
                return;
            }
        }
        bool routed, by_name;
        {
            std::lock_guard<std::mutex> lock(m_);
            bool here = local_.count(id) > 0;
            routed = here || (up_ && routed_remote(id));
            by_name = here || remote_.count(id) > 0;
        }
        if (!routed) {
            done(Check::Nobody, {});
            return;
        }
        start_call(wire::ids::QueryDescriptors, std::make_shared<const Bytes>(wire::string_payload(id)), opt_.call_timeout, std::nullopt, inline_locals,
                   [this, id, by_name, done](std::vector<Bytes> answers) {
                       for (const auto& answer : answers) {
                           Reader r(answer);
                           wire::Descriptor d;
                           if (wire::read_descriptor(r, d) && d.id == id) {
                               wire::Descriptor adopted;
                               {
                                   std::lock_guard<std::mutex> lock(types_m_);
                                   adopted = store_.adopt(d);
                               }
                               done(Check::Ok, std::move(adopted));
                               return;
                           }
                       }
                       done(by_name ? Check::Mismatch : Check::Nobody, {});
                   });
    }

    static std::vector<int32_t> replies(const std::vector<Bytes>& answers) {
        std::vector<int32_t> values;
        for (const auto& a : answers) {
            Reader r(a);
            int32_t v;
            if (r.i32(v)) values.push_back(v);
        }
        return values;
    }

    std::vector<Bytes> protocol_call(const char* id, const Bytes& payload) { return call_blocking(id, payload, CallOptions(opt_.call_timeout)); }

    // Agrees (in, out) as the type of `id` with the network, like the .NET node: the known type must match; otherwise
    // propose it (TryInitiate everywhere, Finalize when all accepted), adopt an equal type agreed meanwhile, retry
    // while an equal proposal is pending, give up on a conflict.
    bool agree(const std::string& id, const Signature& in, const Signature& out, const std::string& description) {
        Gate gate(*this);
        if (!gate) return false;
        while (!stopping_) {
            {
                std::lock_guard<std::mutex> lock(types_m_);
                if (auto d = store_.find(id)) {
                    if (matches(*d, in, out)) return true;
                    log(id + ": the agreed type differs");
                    return false;
                }
            }
            wire::Descriptor proposal;
            proposal.expected = in.entries;
            proposal.id = id;
            proposal.returns = out.entries;
            proposal.description = description;
            proposal.input_annotations = in.annotations;
            proposal.return_annotations = out.annotations;
            {
                std::lock_guard<std::mutex> lock(types_m_);
                proposal.weight = std::uniform_int_distribution<int32_t>(0, 0x7FFFFFFE)(random_);
            }
            auto payload = wire::descriptor_payload(proposal);
            auto initiated = replies(protocol_call(wire::ids::TryInitiate, payload));
            bool all_accepted = true;
            for (int32_t v : initiated) all_accepted = all_accepted && v == Accepted;
            if (all_accepted) {
                for (int32_t v : replies(protocol_call(wire::ids::Finalize, payload))) {
                    if (v < 0) {
                        protocol_call(wire::ids::Abort, payload);
                        log(id + ": a different type was agreed while finalizing");
                        return false;
                    }
                }
                std::lock_guard<std::mutex> lock(types_m_);
                auto d = store_.find(id);
                return d != nullptr && matches(*d, in, out);
            }
            protocol_call(wire::ids::Abort, payload);  // withdraw it wherever it was accepted
            bool conflict = false, established = false;
            for (int32_t v : initiated) {
                conflict = conflict || v == Conflict;
                established = established || v == Established;
            }
            if (conflict) {
                log(id + ": the network has a different type");
                return false;
            }
            if (established) {
                bool adopted = false;
                for (const auto& answer : protocol_call(wire::ids::QueryDescriptors, wire::string_payload(id))) {
                    Reader r(answer);
                    wire::Descriptor d;
                    if (wire::read_descriptor(r, d) && d.same_type(proposal)) {
                        std::lock_guard<std::mutex> lock(types_m_);
                        store_.adopt(d);
                        adopted = true;
                        break;
                    }
                }
                if (adopted) continue;
            }
            std::this_thread::sleep_for(Millis(100));  // an equal proposal is pending: try again
        }
        return false;
    }

    std::shared_ptr<const dyn::Schema> schema_for(const std::vector<std::string>& signature) {
        std::string key = join(signature);
        std::lock_guard<std::mutex> lock(types_m_);
        auto known = schemas_.find(key);
        if (known != schemas_.end()) return known->second;
        std::string why;
        auto s = dyn::Schema::parse(signature, &why);
        if (!s) log("can't read the type " + key + ": " + why);
        schemas_[key] = s;
        return s;
    }

    // The schemas (input, answer) for a dynamic hook or function: proposed from the given signatures if the network
    // has no type yet, else the agreed ones (which must be an event for a hook, a function for a provider).
    std::optional<std::pair<std::shared_ptr<const dyn::Schema>, std::shared_ptr<const dyn::Schema>>> dynamic_schemas(
        const std::string& id, bool event, const std::vector<std::string>& propose_in, const std::vector<std::string>& propose_out,
        const std::string& description) {
        if (!propose_in.empty()) {
            std::string why;
            auto in = dyn::Schema::parse(propose_in, &why);
            auto out = event ? dyn::Schema::parse({"=Void"}) : dyn::Schema::parse(propose_out, &why);
            if (!in || !out) {
                log(id + ": not a NOTES signature: " + why);
                return std::nullopt;
            }
            Signature sin, sout;
            sin.entries = in->canonical();
            sout.entries = out->canonical();
            if (!agree(id, sin, sout, description)) return std::nullopt;
        }
        auto agreed = find_agreed(id);
        if (agreed.first != Check::Ok || agreed.second.is_event() != event) {
            log(id + ": no agreed " + (event ? "event" : "function") + " type to use");
            return std::nullopt;
        }
        auto in = schema_for(agreed.second.expected), out = schema_for(agreed.second.returns);
        if (!in || !out) return std::nullopt;
        return std::make_pair(in, out);
    }

    // Describes namespace `ns` (a prefix route) and those nested in it into `out`; false if it doesn't answer.
    bool describe_tree(const std::string& ns, int depth, const CallOptions& options, std::map<std::string, Endpoint>& out) {
        auto answers = call<detail::NsDescription>(ns + ".$Describe", std::string(), options);
        if (!answers || answers->empty()) return false;
        detail::NsDescription d = (*answers)[0];
        // A nested namespace names things as its own outer network does: respell them as this network reaches them.
        const std::string& local = d.Namespace;
        auto respell = [&](const std::string& id) {
            if (local == ns || ns.size() <= local.size() || ns.compare(ns.size() - local.size() - 1, std::string::npos, "." + local) != 0) return id;
            return id.compare(0, local.size() + 1, local + ".") == 0 ? ns + id.substr(local.size()) : id;
        };
        for (const auto& e : d.Endpoints) {
            Endpoint ep;
            ep.id = respell(e.Id);
            ep.kind = e.Kind;
            ep.type.id = ep.id;
            ep.type.expected = e.Input;
            ep.type.returns = e.Kind == "event" ? std::vector<std::string>{"=Void"} : e.Returns;
            ep.type.description = e.Description;
            for (const auto& a : e.InputAnnotations) ep.type.input_annotations.push_back({a.Record, a.Field, a.Description, a.Min, a.Max, a.Default});
            for (const auto& a : e.ReturnAnnotations) ep.type.return_annotations.push_back({a.Record, a.Field, a.Description, a.Min, a.Max, a.Default});
            out.emplace(ep.id, std::move(ep));
        }
        if (depth > 1) {
            for (const auto& nested : d.Namespaces) describe_tree(respell(nested), depth - 1, options, out);
        }
        return true;
    }

    int64_t now_ms() const { return std::chrono::duration_cast<Millis>(Clock::now().time_since_epoch()).count(); }

    static Bytes int_answer(int32_t v) {
        Writer w;
        w.i32(v);
        return w.out;
    }

    // The NOTES protocol's functions, answered by this leaf like by any .NET node.
    void register_notes() {
        auto with_descriptor = [this](std::function<bool(const wire::Descriptor&, Bytes&)> body) {
            return sync_handler([this, body](const Bytes& input, Bytes& answer) {
                Reader r(input);
                wire::Descriptor d;
                if (!wire::read_descriptor(r, d)) return false;
                std::lock_guard<std::mutex> lock(types_m_);
                return body(d, answer);
            });
        };
        add_call_handler(wire::ids::TryInitiate, with_descriptor([this](const wire::Descriptor& d, Bytes& answer) {
                             answer = int_answer(store_.try_initiate(d, now_ms()));
                             return true;
                         }), true);
        add_call_handler(wire::ids::Finalize, with_descriptor([this](const wire::Descriptor& d, Bytes& answer) {
                             answer = int_answer(store_.finalize(d));
                             return true;
                         }), true);
        add_call_handler(wire::ids::Abort, with_descriptor([this](const wire::Descriptor& d, Bytes& answer) {
                             store_.abort(d);
                             answer = {0};
                             return true;
                         }), true);
        add_call_handler(wire::ids::QueryDescriptors, sync_handler([this](const Bytes& input, Bytes& answer) {
                             Reader r(input);
                             std::string id;
                             if (!r.str(id)) return false;
                             std::lock_guard<std::mutex> lock(types_m_);
                             auto d = store_.find(id);
                             if (d == nullptr) return false;  // unknown: no answer at all
                             answer = wire::descriptor_payload(*d);
                             return true;
                         }), true);
        add_call_handler(wire::ids::QueryDescriptorSet, sync_handler([this](const Bytes& input, Bytes& answer) {
                             Reader r(input);
                             std::vector<std::string> ids;
                             if (!wire::read_strings(r, ids)) return false;
                             std::lock_guard<std::mutex> lock(types_m_);
                             auto found = store_.find_set(ids);
                             if (found.empty()) return false;
                             answer = wire::descriptors_payload(found);
                             return true;
                         }), true);
        add_call_handler(wire::ids::ShareDescriptors, sync_handler([this](const Bytes& input, Bytes& answer) {
                             Reader r(input);
                             std::vector<wire::Descriptor> items;
                             if (!wire::read_descriptors(r, items)) return false;
                             std::lock_guard<std::mutex> lock(types_m_);
                             auto conflicts = store_.share(items);
                             if (conflicts.empty()) return false;
                             answer = wire::strings_payload(conflicts);
                             return true;
                         }), true);
        add_call_handler(wire::ids::StopEvents, sync_handler([this](const Bytes&, Bytes& answer) {
                             std::unique_lock<std::mutex> lock(gate_m_);
                             gate_closed_ = true;
                             gate_cv_.wait(lock, [&] { return inside_ == 0 || stopping_; });  // answer once the operations inside left
                             answer = {0};
                             return true;
                         }), true);
        add_call_handler(wire::ids::AllowEvents, sync_handler([this](const Bytes&, Bytes& answer) {
                             std::vector<std::function<void(bool)>> waiting;
                             {
                                 std::lock_guard<std::mutex> lock(gate_m_);
                                 gate_closed_ = false;
                                 waiting.swap(gate_waiters_);
                                 inside_ += static_cast<int>(waiting.size());
                             }
                             gate_cv_.notify_all();
                             for (auto& w : waiting) post([w] { w(true); });
                             answer = {0};
                             return true;
                         }), true);
        add_call_handler(wire::ids::AwaitReady, sync_handler([this](const Bytes&, Bytes& answer) {
                             std::unique_lock<std::mutex> lock(gate_m_);
                             gate_cv_.wait(lock, [&] { return !gate_closed_ || stopping_; });
                             answer = {0};
                             return true;
                         }), true);
    }

    // ------------------------------------------------------------------ the link's thread

    // Under m_: makes `link` the current link and announces it.
    uint64_t begin_link(std::shared_ptr<Transport> link) {
        link_ = std::move(link);
        up_ = false;
        remote_.clear();
        generation_++;
        last_heard_ms_ = now_ms();
        last_ping_ms_ = 0;
        ping_token_ = 0;
        close_reason_.clear();
        auth_done_ = opt_.user_id.empty();
        authenticated_ = false;
        expected_server_proof_.clear();
        server_realm_.clear();
        send_to(link_, {wire::ids::InterconnectRunning, wire::PackageType::ServerAdminEvent, wire::handshake_payload([&] { auto h = wire::own_handshake(); h.node_name = name_; return h; }())});
        return generation_;
    }

    // Ends a re-dial loop (the link is down and the reader thread is dialing): the reader thread exits.
    void stop_redialing() {
        {
            std::lock_guard<std::mutex> lock(m_);
            if (link_ || !reader_running_) return;
            dialer_ = nullptr;
        }
        link_cv_.notify_all();
        if (reader_.joinable()) reader_.join();
    }

    void notify_link(bool up, const std::string& reason) {
        std::function<void(bool, const std::string&)> handler;
        {
            std::lock_guard<std::mutex> lock(m_);
            handler = on_link_;
        }
        if (handler) dispatch([handler, up, reason] { handler(up, reason); });
    }

    void reader_main() {
        while (true) {
            std::shared_ptr<Transport> link;
            {
                std::lock_guard<std::mutex> lock(m_);
                link = link_;
            }
            std::string reason = link ? read_link(*link) : "no link";
            if (link) link->close();
            bool was_up;
            std::vector<std::shared_ptr<CallState>> waiting;
            {
                std::lock_guard<std::mutex> lock(m_);
                was_up = up_;
                up_ = false;
                link_.reset();
                remote_.clear();
                for (auto& c : active_) {
                    if (c.second->remote) {
                        c.second->remote = false;  // calls complete with what arrived
                        waiting.push_back(c.second);
                    }
                }
            }
            for (auto& c : waiting) complete(c, false);
            link_cv_.notify_all();
            log("link down: " + reason);
            if (was_up) notify_link(false, reason);
            // Re-dial, if asked to, until it works or the leaf stops.
            while (true) {
                std::function<std::unique_ptr<Transport>()> dial;
                {
                    std::unique_lock<std::mutex> lock(m_);
                    if (stopping_ || !dialer_) {
                        reader_running_ = false;
                        return;
                    }
                    link_cv_.wait_for(lock, opt_.reconnect_delay, [&] { return stopping_.load(); });
                    if (stopping_ || !dialer_) {
                        reader_running_ = false;
                        return;
                    }
                    dial = dialer_;
                }
                auto next = dial();
                if (!next) continue;
                std::lock_guard<std::mutex> lock(m_);
                if (stopping_ || !dialer_) {
                    next->close();
                    reader_running_ = false;
                    return;
                }
                begin_link(std::shared_ptr<Transport>(std::move(next)));
                break;
            }
        }
    }

    // Reads frames until the link ends; the reason.
    std::string read_link(Transport& link) {
        wire::FrameParser parser;
        Bytes buffer(64 * 1024);
        while (!stopping_) {
            long got = link.receive(buffer.data(), buffer.size());
            if (got <= 0) {
                std::lock_guard<std::mutex> lock(m_);
                if (!close_reason_.empty()) {
                    std::string why = std::move(close_reason_);
                    close_reason_.clear();
                    return why;
                }
                return got == 0 ? "the router closed the link" : "the link failed";
            }
            last_heard_ms_ = now_ms();
            parser.feed(buffer.data(), static_cast<size_t>(got));
            while (true) {
                wire::Package p;
                std::string why;
                auto result = parser.next(p, why);
                if (result == wire::FrameParser::Result::NeedMore) break;
                if (result == wire::FrameParser::Result::Malformed || !process(p, why)) {
                    send({wire::ids::LinkFault, wire::PackageType::ServerAdminEvent, wire::string_payload(why)});
                    return "malformed input: " + why;
                }
            }
        }
        return "stopped";
    }

    // One package from the router; false (with the reason) if it is malformed.
    bool process(wire::Package& p, std::string& why) {
        using wire::PackageType;
        namespace ids = wire::ids;
        if (p.type == PackageType::ServerAdminEvent) {
            Reader r(p.data);
            if (p.id == ids::InterconnectRunning) {
                wire::Handshake peer;
                if (!wire::read_handshake(p.data, peer)) return fail(why, "InterconnectRunning payload is not a handshake");
                auto own = wire::own_handshake();
                if (!own.accepts(peer)) {
                    std::lock_guard<std::mutex> lock(m_);
                    dialer_ = nullptr;  // re-dialing wouldn't change the router's version
                    return fail(why, "protocol version mismatch: the router is " + peer.describe() + ", this leaf is " + own.describe());
                }
                std::lock_guard<std::mutex> lock(m_);
                router_ = peer;
            }
            if (p.id == ids::Ping) {
                std::lock_guard<std::mutex> lock(m_);
                if (link_) send_to(link_, {ids::Pong, PackageType::ServerAdminEvent, p.data});  // answered at once: the same number back
            } else if (p.id == ids::Pong) {
                Reader pong(p.data);
                uint64_t token;
                std::lock_guard<std::mutex> lock(m_);
                if (pong.le(token) && ping_token_ != 0 && token == ping_token_) {
                    round_trip_ms_ = now_ms() - ping_sent_ms_;
                    ping_token_ = 0;
                }
            } else if (p.id == ids::AuthChallenge) {
                return answer_challenge(p, why);
            } else if (p.id == ids::AuthResult) {
                return check_server_proof(p, why);
            } else if (p.id == ids::InterconnectRunning || p.id == ids::QueryEvents) {
                std::lock_guard<std::mutex> lock(m_);
                // With credentials, what this leaf is interested in is said once the router has proved itself: the router asks after it has.
                if (link_ && auth_done_) send_to(link_, {ids::ListEvents, PackageType::ServerAdminEvent, wire::strings_payload(interest_locked())});
            } else if (p.id == ids::ListEvents) {
                bool open_router = false;
                {
                    std::lock_guard<std::mutex> lock(m_);
                    if (!auth_done_) {
                        if (!opt_.allow_unauthenticated_router) {
                            dialer_ = nullptr;  // dialing again would not change the router
                            return fail(why, "the router did not authenticate: this leaf has credentials and requires its router to prove it knows the shared secret");
                        }
                        auth_done_ = open_router = true;  // a router that does not ask, and this leaf is told to accept that
                    }
                }
                if (open_router) {
                    std::lock_guard<std::mutex> lock(m_);
                    if (link_) send_to(link_, {ids::ListEvents, PackageType::ServerAdminEvent, wire::strings_payload(interest_locked())});
                }
                std::vector<std::string> list;
                if (!wire::read_strings(r, list)) return fail(why, "ListEvents payload is not a list of strings");
                bool share = false, came_up = false;
                {
                    std::lock_guard<std::mutex> lock(m_);
                    remote_.insert(list.begin(), list.end());
                    came_up = !up_;
                    up_ = true;
                    for (const auto& id : list) share = share || id == ids::ShareDescriptors;
                }
                link_cv_.notify_all();
                if (came_up) {
                    log("link up");
                    notify_link(true, "");
                }
                if (share) post([this] { share_descriptors(); });
            } else if (p.id == ids::EventAdded || p.id == ids::EventRemoved) {
                std::string id;
                if (!r.str(id)) return fail(why, p.id + " payload is not a string");
                std::lock_guard<std::mutex> lock(m_);
                if (p.id == ids::EventAdded)
                    remote_.insert(id);
                else
                    remote_.erase(id);
            } else if (p.id == ids::LinkFault) {
                std::string reason;
                r.str(reason);
                log("the router dropped the link: " + reason);
                std::lock_guard<std::mutex> lock(m_);
                if (!up_) dialer_ = nullptr;  // refused before the link was up (an unknown user, a wrong secret): dialing again would not change that
                if (link_) link_->close();
            }
            return true;
        }
        if (p.type == PackageType::FunctionCall) {
            wire::Function f;
            if (!wire::read_function(p.data, f)) return fail(why, "malformed FunctionCall '" + p.id + "'");
            if (f.parameters.id != p.id) return fail(why, "function call for '" + p.id + "' carries parameters for '" + f.parameters.id + "'");
            std::vector<Entry> mine;
            {
                std::lock_guard<std::mutex> lock(m_);
                mine = entries_locked(p.id, true);
            }
            answer_call(std::move(f), mine);
            return true;
        }
        if (p.type == PackageType::FunctionReturn) {
            wire::Function f;
            if (!wire::read_function(p.data, f)) return fail(why, "malformed FunctionReturn '" + p.id + "'");
            std::shared_ptr<CallState> st;
            {
                std::lock_guard<std::mutex> lock(m_);
                auto found = active_.find(f.handle.call_id);
                if (found == active_.end() || !found->second->remote) return true;  // timed out or cancelled already
                st = found->second;
                for (auto& r : f.returns) st->answers.push_back(std::move(r.data));
                st->remote = false;
            }
            complete(st, false);
            return true;
        }
        if (p.type == PackageType::Data) {
            std::vector<Entry> mine;
            {
                std::lock_guard<std::mutex> lock(m_);
                mine = entries_locked(p.id, false);
            }
            auto data = std::make_shared<const Bytes>(std::move(p.data));
            for (auto& e : mine) dispatch([this, e, data] { invoke_raw(e, *data); });
        }
        return true;
    }

    // Keepalive watchdog: ping a router that has been quiet, close the link on one that has been silent too long.
    void keepalive_main() {
        std::unique_lock<std::mutex> lock(ka_m_);
        const Millis period = (std::max)(Millis(5), opt_.keepalive_interval / 4);
        while (!stopping_) {
            ka_cv_.wait_for(lock, period);
            if (stopping_) break;
            lock.unlock();
            keepalive_tick();
            lock.lock();
        }
    }

    void keepalive_tick() {
        const int64_t interval = opt_.keepalive_interval.count();
        const int64_t timeout = opt_.keepalive_timeout.count() > 0 ? opt_.keepalive_timeout.count() : interval * 3;
        std::shared_ptr<Transport> link;
        bool ping = false;
        uint64_t token = 0;
        {
            std::lock_guard<std::mutex> lock(m_);
            if (!link_ || !up_ || router_.version < 2) return;  // a router that has not said it answers pings is never pinged
            const int64_t now = now_ms();
            const int64_t silent = now - last_heard_ms_;
            if (silent >= timeout) {
                close_reason_ = "keepalive timeout: nothing from the router for " + std::to_string(silent) + " ms (limit " + std::to_string(timeout) + " ms)";
                log(close_reason_);
                link_->close();  // wakes the reader, which reports the link down and dials again if asked to
                return;
            }
            if (silent >= interval && now - last_ping_ms_ >= interval) {
                last_ping_ms_ = ping_sent_ms_ = now;
                token = ping_token_ = static_cast<uint64_t>(now);
                link = link_;
                ping = true;
            }
        }
        if (ping) {
            Writer w;
            w.le(token);
            send_to(link, {wire::ids::Ping, wire::PackageType::ServerAdminEvent, w.out});
        }
    }

    // The router asks who this leaf is: answer with the proof, and remember what the router must prove in return.
    bool answer_challenge(const wire::Package& p, std::string& why) {
        std::vector<uint8_t> server_nonce;
        std::string realm;
        if (!wire::read_challenge(p.data, server_nonce, realm) || server_nonce.size() != auth::nonce_size) return fail(why, "AuthChallenge payload is not a challenge");
        if (opt_.user_id.empty()) {
            std::lock_guard<std::mutex> lock(m_);
            dialer_ = nullptr;
            return fail(why, "the router requires authentication and this leaf has no credentials");
        }
        std::random_device device;
        std::vector<uint8_t> client_nonce(auth::nonce_size);
        for (auto& b : client_nonce) b = static_cast<uint8_t>(device());
        auto proof = auth::proof(auth::client_label, opt_.secret, server_nonce, client_nonce, opt_.user_id, realm);
        std::lock_guard<std::mutex> lock(m_);
        server_realm_ = realm;
        expected_server_proof_ = auth::proof(auth::server_label, opt_.secret, server_nonce, client_nonce, opt_.user_id, realm);
        if (link_) send_to(link_, {wire::ids::AuthResponse, wire::PackageType::ServerAdminEvent, wire::response_payload(opt_.user_id, client_nonce, proof)});
        return true;
    }

    // The router proves it knows the secret too. One that cannot is not the router this leaf meant to talk to.
    bool check_server_proof(const wire::Package& p, std::string& why) {
        std::vector<uint8_t> proof;
        if (!wire::read_result(p.data, proof)) return fail(why, "AuthResult payload is not a proof");
        std::lock_guard<std::mutex> lock(m_);
        if (expected_server_proof_.empty() || !auth::same(proof, expected_server_proof_)) {
            dialer_ = nullptr;
            return fail(why, "the router did not prove it knows the shared secret");
        }
        expected_server_proof_.clear();
        auth_done_ = authenticated_ = true;
        return true;
    }

    static bool fail(std::string& why, const std::string& reason) {
        why = reason;
        return false;
    }

    // Runs this leaf's handlers for a call from the router and sends their answers back once all answered (always,
    // even none). User handlers are dispatched; the NOTES responders run on the leaf's threads.
    void answer_call(wire::Function f, const std::vector<Entry>& mine) {
        struct Collect {
            std::mutex m;
            size_t left;
            wire::Function f;
        };
        if (f.handle.unacknowledged()) {
            // Nobody waits for the answer: run the handlers, one event ID's calls in the order they arrived, and send nothing.
            auto input = std::make_shared<const Bytes>(std::move(f.parameters.data));
            for (const auto& e : mine) run_in_lane(f.parameters.id, [this, e, input] { invoke(e, *input, [](std::optional<Bytes>) {}); });
            return;
        }
        auto c = std::make_shared<Collect>();
        c->left = mine.size();
        c->f = std::move(f);
        if (mine.empty()) {
            send({c->f.parameters.id, wire::PackageType::FunctionReturn, wire::function_payload(c->f)});
            return;
        }
        // The answer carries no parameters (an empty package for the same ID): the caller has them, and the router's readers use
        // only the handle and the answers.
        auto input = std::make_shared<const Bytes>(std::move(c->f.parameters.data));
        c->f.parameters.data.clear();
        for (const auto& e : mine) {
            Reply reply = once([this, c](std::optional<Bytes> a) {
                bool last;
                {
                    std::lock_guard<std::mutex> lock(c->m);
                    if (a && !a->empty()) c->f.returns.push_back({c->f.parameters.id, wire::PackageType::FunctionReturn, std::move(*a)});
                    last = --c->left == 0;
                }
                if (last) send({c->f.parameters.id, wire::PackageType::FunctionReturn, wire::function_payload(c->f)});
            });
            auto job = [this, e, input, reply] { invoke(e, *input, reply); };
            if (e.internal)
                post(std::move(job));
            else
                dispatch(std::move(job));
        }
    }

    // After a link comes up: offer every known type to the network (it commits the ones it lacks).
    void share_descriptors() {
        std::vector<wire::Descriptor> known;
        {
            std::lock_guard<std::mutex> lock(types_m_);
            known = store_.all();
        }
        if (known.empty()) return;
        for (const auto& c : protocol_call(wire::ids::ShareDescriptors, wire::descriptors_payload(known))) {
            Reader r(c);
            std::vector<std::string> ids;
            if (wire::read_strings(r, ids)) {
                for (const auto& id : ids) log("the network has a different type for " + id);
            }
        }
    }

    // ------------------------------------------------------------------ threads

    // Arms a call's deadline.
    void arm(Clock::time_point at, const std::shared_ptr<CallState>& st) {
        std::lock_guard<std::mutex> lock(timer_m_);
        if (st->finished) return;
        st->timer = timers_.emplace(at, st);
        st->armed = true;
        timer_cv_.notify_one();
    }

    void timer_main() {
        std::unique_lock<std::mutex> lock(timer_m_);
        while (!stopping_) {
            if (timers_.empty()) {
                timer_cv_.wait(lock);
                continue;
            }
            auto first = timers_.begin()->first;
            if (Clock::now() < first) {
                timer_cv_.wait_until(lock, first);
                continue;
            }
            auto weak = timers_.begin()->second;
            timers_.erase(timers_.begin());
            auto st = weak.lock();
            if (st) st->armed = false;
            lock.unlock();
            if (st) complete(st, true);  // the time is up: complete with what arrived
            lock.lock();
        }
    }

    // One lane per event ID for calls nobody answers: its jobs run one after the other, in the order they were queued, so a stream of
    // readings does not overtake itself on the handler threads. Lanes of different IDs run side by side; a lane exists only while it
    // has jobs.
    void run_in_lane(const std::string& id, std::function<void()> job) {
        bool start;
        {
            std::lock_guard<std::mutex> lock(lane_m_);
            auto it = lanes_.find(id);
            start = it == lanes_.end();
            if (start) it = lanes_.emplace(id, std::deque<std::function<void()>>{}).first;
            it->second.push_back(std::move(job));
        }
        if (start) dispatch([this, id] { drain_lane(id); });
    }

    void drain_lane(const std::string& id) {
        while (true) {
            std::function<void()> job;
            {
                std::lock_guard<std::mutex> lock(lane_m_);
                auto it = lanes_.find(id);
                if (it->second.empty()) {
                    lanes_.erase(it);
                    return;
                }
                job = std::move(it->second.front());
                it->second.pop_front();
            }
            job();
        }
    }

    // Hands `bytes` to the router (if it routes `id` onward) and to this leaf's own subscribers, and does not wait for anyone.
    void publish_bytes(const std::string& id, Bytes bytes) {
        std::shared_ptr<Transport> link;
        std::vector<Entry> mine;
        uint64_t number;
        {
            std::lock_guard<std::mutex> lock(m_);
            if (stopping_) return;
            number = next_call_++;
            if (up_ && routed_remote(id)) link = link_;
            mine = entries_locked(id, true);
        }
        if (link) {
            if (!send_frame_to(link, wire::call_frame(id, bytes, {number | wire::Handle::unacknowledged_flag, 0}))) log("could not publish " + id + ": the link is down");
        }
        if (!mine.empty()) {
            auto input = std::make_shared<const Bytes>(std::move(bytes));
            for (auto& e : mine) run_in_lane(id, [this, e, input] { invoke(e, *input, [](std::optional<Bytes>) {}); });
        }
    }

    // Runs a job on the leaf's threads (the leaf's own work, and handlers with Dispatch::Threads).
    void post(std::function<void()> job) {
        std::lock_guard<std::mutex> lock(work_m_);
        if (stopping_) return;
        jobs_.push_back(std::move(job));
        if (jobs_.size() > idle_ && workers_.size() < opt_.max_workers) workers_.emplace_back(&Leaf::worker_main, this);
        work_cv_.notify_one();
    }

    // Runs application code (handlers, callbacks) where options.dispatch says.
    void dispatch(std::function<void()> job) {
        if (opt_.dispatch == Dispatch::Threads) {
            post(std::move(job));
            return;
        }
        {
            std::lock_guard<std::mutex> lock(user_m_);
            if (stopping_) return;
            user_jobs_.push_back(std::move(job));
        }
        user_cv_.notify_all();
        if (opt_.wakeup) opt_.wakeup();
    }

    void worker_main() {
        std::unique_lock<std::mutex> lock(work_m_);
        while (true) {
            idle_++;
            work_cv_.wait(lock, [&] { return stopping_ || !jobs_.empty(); });
            idle_--;
            if (jobs_.empty()) return;  // stopping
            auto job = std::move(jobs_.front());
            jobs_.pop_front();
            lock.unlock();
            job();
            lock.lock();
        }
    }
};

}  // namespace evn
