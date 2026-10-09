#include "northpass/split.hpp"
#include "northpass/options.hpp"
#include <algorithm>
#include <iostream>
#include <ctime>
#include <stdexcept>
using namespace northpass;
namespace {
void require(bool ok) { if (!ok) throw std::runtime_error("Northpass Split assertion failed"); }
void word(std::vector<std::uint8_t>& p, std::size_t at, std::size_t n) { p[at] = static_cast<std::uint8_t>(n >> 8); p[at + 1] = static_cast<std::uint8_t>(n); }
void dword(std::vector<std::uint8_t>& p, std::size_t at, std::uint32_t n) { for (unsigned i = 0; i < 4; ++i) p[at + i] = static_cast<std::uint8_t>(n >> (24 - i * 8)); }
std::vector<std::uint8_t> hello(std::size_t padding = 0) {
    std::vector<std::uint8_t> p(padding ? 56 + padding : 50);
    p[0] = 22; p[1] = 3; p[2] = 1; word(p, 3, p.size() - 5);
    p[5] = 1; p[6] = static_cast<std::uint8_t>((p.size() - 9) >> 16); word(p, 7, p.size() - 9);
    p[9] = 3; p[10] = 3; word(p, 44, 2); p[46] = 0x13; p[47] = 1; p[48] = 1;
    if (padding) { word(p, 50, padding + 4); word(p, 52, 21); word(p, 54, padding); }
    return p;
}
std::vector<std::uint8_t> packet(std::span<const std::uint8_t> payload, bool ipv6 = false, std::uint32_t seq = 1001,
    std::uint8_t flags = 0x18, bool reverse = false, std::uint32_t ack = 2001, bool timestamp = false) {
    const std::size_t ip = ipv6 ? 40 : 20, tcp = timestamp ? 32 : 20;
    std::vector<std::uint8_t> p(ip + tcp + payload.size());
    p[0] = ipv6 ? 0x60 : 0x45;
    if (ipv6) { word(p, 4, p.size() - 40); p[6] = 6; p[7] = 64; p[23] = p[39] = 1; }
    else { word(p, 2, p.size()); p[6] = 0x40; p[8] = 64; p[9] = 6; p[12] = p[16] = 127; p[15] = p[19] = 1; }
    word(p, ip, reverse ? 55000 : 55001); word(p, ip + 2, reverse ? 55001 : 55000);
    dword(p, ip + 4, seq); dword(p, ip + 8, ack); p[ip + 12] = static_cast<std::uint8_t>(tcp / 4 << 4); p[ip + 13] = flags; word(p, ip + 14, 60000);
    if (timestamp) { p[ip + 20] = p[ip + 21] = 1; p[ip + 22] = 8; p[ip + 23] = 10; dword(p, ip + 24, 42); dword(p, ip + 28, 43); }
    std::copy(payload.begin(), payload.end(), p.begin() + static_cast<std::ptrdiff_t>(ip + tcp));
    if (!payload.empty() && flags == 0x18) require(calculate_tcp_checksums(p));
    return p;
}
const FlowSummary* handshake(FlowTracker& tracker, bool ipv6 = false, std::uint32_t start = 1000) {
    auto syn = packet({}, ipv6, start, 2, false, 0), synack = packet({}, ipv6, 2000, 0x12, true, start + 1), ack = packet({}, ipv6, start + 1, 0x10);
    tracker.observe(classify(syn), syn.size(), Clock::now()); tracker.observe(classify(synack), synack.size(), Clock::now());
    return tracker.observe(classify(ack), ack.size(), Clock::now());
}
std::vector<std::uint8_t> reconstruction(const SegmentProposal& p) {
    std::vector<std::uint8_t> result;
    for (const auto& segment : p.packets) { const auto v = classify(segment); result.insert(result.end(), v.payload.begin(), v.payload.end()); }
    return result;
}
void test(std::string_view name) {
    NorthpassSplit strategy; strategy.initialize({}); auto payload = hello(300), bytes = packet(payload);
    FlowTracker tracker; const auto* state = handshake(tracker);
    auto decision = strategy.propose(bytes, state); require(decision.proposal.has_value());
    if (name == "split_framing") {
        require(parse_client_hello(hello()).state == ClientHelloState::Complete);
        require(parse_client_hello(payload).state == ClientHelloState::Complete);
        for (std::size_t i = 0; i < payload.size(); ++i) require(parse_client_hello(std::span(payload).first(i)).state != ClientHelloState::Complete);
        for (auto at : {0u, 1u, 3u, 5u, 6u, 7u, 43u, 44u, 48u, 50u, 54u}) {
            auto malformed = payload; malformed[at] = 255; require(parse_client_hello(malformed).state != ClientHelloState::Complete);
        }
        auto extra = payload; extra.push_back(0); require(parse_client_hello(extra).state == ClientHelloState::Unsupported);
        // Bounds exploration includes random extension and handshake lengths.
        for (unsigned i = 0; i < 65536; ++i) { auto p = hello(10); std::fill(p.begin() + 56, p.end(), std::uint8_t{255}); word(p, 54, i); require((parse_client_hello(p).state == ClientHelloState::Complete) == (i == 10)); }
    } else if (name == "split_sequences") {
        for (bool ipv6 : {false, true}) for (bool timestamps : {false, true}) {
            auto original = packet(payload, ipv6, 0xfffffffe, 0x18, false, 2001, timestamps); FlowTracker flow;
            auto p = strategy.propose(original, handshake(flow, ipv6, 0xfffffffd)); require(p.proposal.has_value());
            std::uint32_t sequence = 0xfffffffe;
            for (std::size_t i = 0; i < p.proposal->packets.size(); ++i) {
                const auto& segment = p.proposal->packets[i]; const auto v = classify(segment);
                require(v.sequence == sequence && v.acknowledgement == 2001 && v.source == classify(original).source);
                require((v.tcp_flags & 8) == (i + 1 == p.proposal->packets.size() ? 8 : 0));
                require(observe_checksum(segment, {true, true, false, ipv6, true, true, false}) == ChecksumObservation::Valid);
                sequence += static_cast<std::uint32_t>(v.payload.size());
            }
            require(reconstruction(*p.proposal) == payload);
        }
    } else if (name == "split_transaction") {
        SegmentTransaction tx(bytes, strategy.transaction_configuration()); require(tx.propose(*decision.proposal)); require(tx.segments().empty()); require(tx.commit());
        bytes.back() ^= 1; require(tx.original().back() != bytes.back()); require(tx.rollback());
        require(tx.propose(*decision.proposal) && tx.commit()); require(tx.record_sent(0)); require(!tx.rollback() && !tx.fallback_allowed());
        require(!tx.record_sent(0)); tx.send_failed(); require(tx.failure() == SegmentFailure::Transmission && !tx.record_sent(1) && !tx.propose(*decision.proposal));
        SegmentTransaction uncertain(packet(payload), strategy.transaction_configuration()); require(uncertain.propose(*decision.proposal) && uncertain.commit());
        uncertain.send_failed(); require(!uncertain.rollback() && !uncertain.fallback_allowed()); // even ambiguous FIRST send is irreversible
    } else if (name == "split_rejection") {
        for (unsigned kind = 0; kind < 8; ++kind) {
            auto invalid = *decision.proposal;
            if (kind == 0) invalid.version = 3;
            if (kind == 1) invalid.packets[1] = invalid.packets[0]; // overlap
            if (kind == 2) invalid.packets[1].back() ^= 1;
            if (kind == 3) invalid.packets[1][24] ^= 1; // sequence
            if (kind == 4) invalid.packets[0][33] |= 8; // early PSH
            if (kind == 5) invalid.packets[1][36] ^= 1; // TCP checksum
            if (kind == 6) invalid.packets.pop_back();
            if (kind == 7) std::swap(invalid.packets[0], invalid.packets[1]);
            SegmentTransaction tx(bytes, strategy.transaction_configuration()); require(tx.propose(*decision.proposal) && tx.commit());
            require(!tx.propose(invalid) && !tx.committed() && tx.segments().empty() && tx.fallback_allowed());
            require(std::equal(bytes.begin(), bytes.end(), tx.original().begin()));
        }
    } else if (name == "split_state") {
        for (bool ipv6 : {false, true}) {
            auto p = packet(payload, ipv6); const auto original = p; const auto tcp = classify(p).transport_offset;
            PacketMetadata m{true, true, false, ipv6, true, true, false};
            require(observe_split_input_checksum(p, m) == ChecksumObservation::Valid);
            if (!ipv6) p[10] = p[11] = 0;
            p[tcp + 16] = p[tcp + 17] = 0; const auto absent = p;
            require(observe_split_input_checksum(p, m) == ChecksumObservation::OffloadUnverified && p == absent);
            p[tcp + 16] = 1; require(observe_split_input_checksum(p, m) == ChecksumObservation::Invalid);
            p = absent; m.loopback = false; require(observe_split_input_checksum(p, m) == ChecksumObservation::Invalid);
            p = original; p[tcp + 16] ^= 1; m.loopback = true; require(observe_split_input_checksum(p, m) == ChecksumObservation::Invalid);
        }
        require(strategy.propose(bytes, nullptr).rejection == SplitRejection::UnknownState);
        auto bad = *state; bad.tcp = TcpObservation::Traffic; require(!strategy.propose(bytes, &bad).proposal);
        bad = *state; ++bad.syn_sequence[static_cast<std::size_t>(bad.syn_direction)]; require(!strategy.propose(bytes, &bad).proposal);
        for (auto flags : {1u, 2u, 4u, 0x38u, 0x58u, 0x98u}) { auto p = bytes; p[33] = static_cast<std::uint8_t>(flags); require(!strategy.propose(p, state).proposal); }
        auto p = bytes; p[6] = 0x20; require(!strategy.propose(p, state).proposal);
        p = bytes; p[32] |= 1; require(!strategy.propose(p, state).proposal);
        p = packet(payload, false, 1001, 0x18, false, 2001, true); p[42] = 19; require(!strategy.propose(p, state).proposal); // TCP MD5 option
        p = packet(payload, true); p[6] = 44; require(!strategy.propose(p, state).proposal);
    } else if (name == "split_retransmission") {
        tracker.observe(classify(bytes), bytes.size(), Clock::now());
        const auto* retransmitted = tracker.observe(classify(bytes), bytes.size(), Clock::now());
        auto again = strategy.propose(bytes, retransmitted); require(again.proposal.has_value() && again.retransmission);
        require(again.proposal->packets == decision.proposal->packets); // duplicates have deterministic segmentation
        auto partial = packet(std::span(payload).subspan(9), false, 1010); require(!strategy.propose(partial, retransmitted).proposal);
        auto reordered = packet(payload, false, 1002); require(!strategy.propose(reordered, retransmitted).proposal);
    } else if (name == "split_mtu") {
        for (bool ipv6 : {false, true}) for (auto size : {1u, 500u, 1200u, 1440u, 4000u, 16000u}) {
            auto data = hello(size), p = packet(data, ipv6); FlowTracker flow; strategy.initialize({4, 1280, 9, TransformScope::Synthetic});
            auto d = strategy.propose(p, handshake(flow, ipv6)); require(d.proposal.has_value());
            SegmentTransaction tx(p, strategy.transaction_configuration()); require(tx.propose(*d.proposal) && tx.commit());
            for (const auto& segment : tx.segments()) require(segment.size() <= 1280);
            require(reconstruction(*d.proposal) == data);
        }
    } else if (name == "split_simulator") {
        require(simulator_recognizes_client_hello(bytes));
        for (const auto& segment : decision.proposal->packets) require(!simulator_recognizes_client_hello(segment));
        require(parse_client_hello(reconstruction(*decision.proposal)).state == ClientHelloState::Complete);
        std::cout << "SYNTHETIC toy packet-local simulator: original recognized, split not recognized; exact reassembly valid. No real DPI conclusion.\n";
    } else if (name == "split_lifecycle") {
        const std::vector<std::string_view> lab{"--mode", "loopback", "--port", "55000", "--protocol", "tcp", "--parent-pid", "1", "--stdio-control", "--test-capability", "--lab-split", "--lab-seconds", "20"};
        require(parse_options(lab).lab_split);
        for (auto key : {"--port", "--protocol", "--lab-seconds", "--parent-pid"}) {
            auto bad = lab; const auto at = std::find(bad.begin(), bad.end(), key); *(at + 1) = key == std::string_view("--protocol") ? "udp" : "0";
            bool rejected = false; try { parse_options(bad); } catch (const std::invalid_argument&) { rejected = true; } require(rejected);
        }
        auto bad = lab; bad.erase(std::find(bad.begin(), bad.end(), "--test-capability"));
        bool unauthorized = false; try { parse_options(bad); } catch (const std::invalid_argument&) { unauthorized = true; } require(unauthorized);
        for (auto c : {SplitConfiguration{3}, {4, 1279}, {4, 1501}, {4, 1500, 0}, {4, 1500, 65}, {4, 1500, 9, TransformScope::Live}}) {
            bool refused = false; try { strategy.initialize(c); } catch (const std::invalid_argument&) { refused = true; } require(refused);
        }
        strategy.initialize({}); strategy.shutdown(); bool stopped = false; try { strategy.propose(bytes, state); } catch (const std::logic_error&) { stopped = true; } require(stopped);
        bool refused = false; try { SegmentTransaction tx(bytes, {4, TransformScope::Live, TransformCapability::TcpSegmentation, 1500}); } catch (const std::invalid_argument&) { refused = true; } require(refused);
    } else if (name == "split_performance") {
        auto start = Clock::now(); auto cpu = std::clock(); std::size_t accepted{};
        for (unsigned i = 0; i < 2000; ++i) { auto p = strategy.propose(bytes, state); SegmentTransaction tx(bytes, strategy.transaction_configuration()); if (p.proposal && tx.propose(*p.proposal) && tx.commit()) ++accepted; }
        auto seconds = std::chrono::duration<double>(Clock::now() - start).count(); require(accepted == 2000 && seconds < 20);
        std::cout << "SYNTHETIC split proposed=2000 accepted=" << accepted << " rejected=0 seconds=" << seconds << " latency_us=" << seconds * 1e6 / accepted << " cpu_seconds=" << static_cast<double>(std::clock() - cpu) / CLOCKS_PER_SEC << " packet_bytes=" << bytes.size() << "; no driver/hardware measurement\n";
    } else throw std::runtime_error("Unknown group");
}
}
int main(int argc, char** argv) { try { if (argc != 2) throw std::runtime_error("Group required"); test(argv[1]); return 0; } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; } }
