// EVent for C++: the NOTES type store. What one participant knows about the network's agreed types, and how it
// answers the agreement protocol (TryInitiate / Finalize / Abort and the descriptor queries).
//
// Part of the portable core: C++17, no exceptions thrown, no I/O, no threads (the caller locks and gives the time).
#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "wire.hpp"

namespace evn {

/// Answers to TryInitiateNOTESDescriptor.
enum InitiateReply : int32_t {
    Conflict = -1,     // a different type is established, or pending and not expired
    Accepted = 0,      // held as pending
    Pending = 1,       // an equal proposal that outranks this one is pending: retry shortly
    Established = 2,   // an equal type is established already: fetch and adopt it
};

class TypeStore {
public:
    /// How long an accepted proposal blocks competing ones (a proposer that dies mid-agreement doesn't block forever).
    int64_t proposal_ttl_ms = 5000;

    int32_t try_initiate(const wire::Descriptor& d, int64_t now_ms) {
        auto known = established_.find(d.id);
        if (known != established_.end()) return known->second.same_type(d) ? Established : Conflict;
        auto pending = live_pending(d.id, now_ms);
        if (pending != nullptr) {
            if (!pending->same_type(d)) return Conflict;
            if (pending->weight < d.weight) {
                set_pending(d, now_ms);
                return Accepted;
            }
            return Pending;
        }
        set_pending(d, now_ms);
        return Accepted;
    }

    /// Commits exactly this proposal: 1, or -1 if a different type is established.
    int32_t finalize(const wire::Descriptor& d) {
        auto known = established_.find(d.id);
        if (known != established_.end()) return known->second.same_type(d) ? 1 : -1;
        established_[d.id] = d;
        pending_.erase(d.id);
        return 1;
    }

    /// Withdraws a proposal, only if it is exactly the pending one (same type and weight).
    void abort(const wire::Descriptor& d) {
        auto pending = pending_.find(d.id);
        if (pending != pending_.end() && pending->second.descriptor.same_proposal(d)) pending_.erase(pending);
    }

    /// Adopts a descriptor the network agreed (if none is known for its ID yet); the one known afterwards.
    const wire::Descriptor& adopt(const wire::Descriptor& d) { return established_.emplace(d.id, d).first->second; }

    const wire::Descriptor* find(const std::string& id) const {
        auto known = established_.find(id);
        return known == established_.end() ? nullptr : &known->second;
    }

    std::vector<wire::Descriptor> find_set(const std::vector<std::string>& ids) const {
        std::vector<wire::Descriptor> found;
        for (const auto& id : ids) {
            if (auto d = find(id)) found.push_back(*d);
        }
        return found;
    }

    /// Commits every descriptor not known yet; the IDs whose known type differs.
    std::vector<std::string> share(const std::vector<wire::Descriptor>& items) {
        std::vector<std::string> conflicts;
        for (const auto& d : items) {
            if (finalize(d) < 0) conflicts.push_back(d.id);
        }
        return conflicts;
    }

    std::vector<wire::Descriptor> all() const {
        std::vector<wire::Descriptor> items;
        for (const auto& known : established_) items.push_back(known.second);
        return items;
    }

    bool empty() const { return established_.empty(); }

private:
    struct PendingEntry {
        wire::Descriptor descriptor;
        int64_t expires_ms;
    };
    std::map<std::string, wire::Descriptor> established_;
    std::map<std::string, PendingEntry> pending_;

    const wire::Descriptor* live_pending(const std::string& id, int64_t now_ms) {
        auto pending = pending_.find(id);
        if (pending == pending_.end()) return nullptr;
        if (now_ms < pending->second.expires_ms) return &pending->second.descriptor;
        pending_.erase(pending);
        return nullptr;
    }

    void set_pending(const wire::Descriptor& d, int64_t now_ms) { pending_[d.id] = {d, now_ms + proposal_ttl_ms}; }
};

}  // namespace evn
