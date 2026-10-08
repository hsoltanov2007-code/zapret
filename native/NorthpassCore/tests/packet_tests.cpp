#include "northpass/flow.hpp"
#include "northpass/options.hpp"
#include <algorithm>
#include <iostream>
#include <random>
#include <stdexcept>
#include <vector>
using namespace northpass;
namespace {
void require(bool condition) { if (!condition) throw std::runtime_error("Assertion failed"); }
void put16(std::vector<std::uint8_t>& p, std::size_t at, std::size_t value) {
    p[at] = static_cast<std::uint8_t>(value >> 8); p[at + 1] = static_cast<std::uint8_t>(value);
}
std::vector<std::uint8_t> packet(bool ipv6 = false, bool udp = false, std::size_t payload = 0) {
    const std::size_t ip = ipv6 ? 40 : 20, transport = udp ? 8 : 20;
    std::vector<std::uint8_t> p(ip + transport + payload);
    p[0] = ipv6 ? 0x60 : 0x45;
    if (ipv6) { put16(p, 4, transport + payload); p[6] = udp ? 17 : 6; p[7] = 64; p[23] = 1; p[39] = 2; }
    else { put16(p, 2, p.size()); p[8] = 64; p[9] = udp ? 17 : 6; p[12] = 127; p[15] = 1; p[16] = 127; p[19] = 2; }
    put16(p, ip, 52001); put16(p, ip + 2, 52002);
    if (udp) put16(p, ip + 4, transport + payload);
    else { p[ip + 12] = 0x50; p[ip + 13] = 2; }
    return p;
}
void check(std::string_view name) {
    if (name == "ipv4") {
        auto p = packet(); auto v = classify(p);
        require(v.state == ParseState::Parsed && v.ip_version == 4 && v.source.port == 52001 && v.destination.port == 52002);
        p.insert(p.begin() + 20, 4, 0); p[0] = 0x46; put16(p, 2, p.size()); require(classify(p).state == ParseState::Parsed);
        p[0] = 0x4f; require(classify(p).state == ParseState::Malformed);
    } else if (name == "ipv6") {
        auto p = packet(true); require(classify(p).state == ParseState::Parsed);
        p.insert(p.begin() + 40, 8, 0); p[6] = 0; p[40] = 6; put16(p, 4, p.size() - 40);
        require(classify(p).transport == Transport::Tcp);
        p[41] = 255; require(classify(p).state == ParseState::Malformed);
        put16(p, 4, 0); require(classify(p).state == ParseState::Unsupported);
    } else if (name == "tcp") {
        auto p = packet(false, false, 3); auto v = classify(p);
        require(v.transport == Transport::Tcp && v.tcp_flags == 2 && v.payload.size() == 3);
        p[32] = 0x40; require(classify(p).state == ParseState::Malformed);
        p[32] = 0xf0; require(classify(p).state == ParseState::Malformed);
    } else if (name == "udp") {
        auto p = packet(false, true, 4); require(classify(p).transport == Transport::Udp && classify(p).payload.size() == 4);
        put16(p, 24, 7); require(classify(p).state == ParseState::Malformed);
        put16(p, 24, 99); require(classify(p).state == ParseState::Malformed);
        auto v6 = packet(true, true); require(classify(v6).state == ParseState::Parsed);
    } else if (name == "tls") {
        std::vector<std::uint8_t> hello(43); hello[0] = 22; hello[1] = 3; hello[2] = 1; put16(hello, 3, 38); hello[5] = 1; hello[8] = 34;
        require(classify_tls(hello) == TlsKind::ClientHelloFraming);
        for (std::size_t size = 0; size < hello.size(); ++size) require(classify_tls(std::span(hello).first(size)) == TlsKind::Unknown);
        auto p = packet(false, false, hello.size()); std::copy(hello.begin(), hello.end(), p.begin() + 40);
        require(classify(p).tls == TlsKind::ClientHelloFraming);
        hello[0] = 23; require(classify_tls(hello) == TlsKind::RecordFraming);
        hello[1] = 2; require(classify_tls(hello) == TlsKind::Unknown);
        p = packet(); put16(p, 22, 443); require(classify(p).tls == TlsKind::Unknown);
    } else if (name == "fragments") {
        auto p = packet(false, false, 4); put16(p, 6, 0x2000); require(classify(p).state == ParseState::Fragment);
        put16(p, 6, 1); require(classify(p).state == ParseState::Fragment);
        put16(p, 6, 0x4000); require(classify(p).state == ParseState::Parsed);
        auto v6 = packet(true, false, 4); v6.insert(v6.begin() + 40, 8, 0); v6[6] = 44; v6[40] = 6; v6[43] = 1; put16(v6, 4, v6.size() - 40); require(classify(v6).state == ParseState::Fragment);
    } else if (name == "malformed") {
        auto p = packet(); for (std::size_t size = 0; size < p.size(); ++size) require(classify(std::span(p).first(size)).state == ParseState::Malformed);
        p[9] = 1; require(classify(p).state == ParseState::Unsupported);
        put16(p, 2, 19); require(classify(p).state == ParseState::Malformed);
        auto v6 = packet(true); for (int i = 0; i < 9; ++i) { v6.insert(v6.begin() + 40, 8, 0); v6[40] = i == 0 ? 6 : 0; }
        v6[6] = 0; put16(v6, 4, v6.size() - 40); require(classify(v6).state == ParseState::Malformed);
    } else if (name == "tracking") {
        FlowTracker tracker(2); auto now = Clock::now(); auto v = classify(packet());
        require(tracker.observe(v, 40, now)->tcp == TcpObservation::Syn);
        std::swap(v.source, v.destination); v.tcp_flags = 0x12; v.acknowledgement = 1; require(tracker.observe(v, 40, now)->tcp == TcpObservation::SynAck);
        v.tcp_flags = 0x10; require(tracker.observe(v, 40, now)->tcp == TcpObservation::SynAck);
        std::swap(v.source, v.destination); v.tcp_flags = 0x10;
        const auto* flow = tracker.observe(v, 40, now); require(flow->tcp == TcpObservation::Established && flow->packets[0] == 2 && flow->packets[1] == 2 && tracker.size() == 1);
        v.tcp_flags = 1; require(tracker.observe(v, 40, now)->tcp == TcpObservation::Closing);
        v.tcp_flags = 4; require(tracker.observe(v, 40, now)->tcp == TcpObservation::Reset);
        v.source.port++; tracker.observe(v, 40, now); v.source.port++; tracker.observe(v, 40, now); require(tracker.size() == 2);
        tracker.expire(now + std::chrono::seconds(120)); require(tracker.size() == 0);
        v.transport = Transport::Udp; tracker.observe(v, 40, now); tracker.expire(now + std::chrono::seconds(30)); require(tracker.size() == 0);
        v.state = ParseState::Malformed; require(tracker.observe(v, 40, now) == nullptr);
    } else if (name == "invariant") {
        PassThroughStrategy strategy; PacketProcessor processor(strategy, 16); std::mt19937 generator(1729);
        for (int i = 0; i < 30000; ++i) {
            auto p = i % 3 == 0 ? packet(i % 2 == 0, i % 4 == 0, 64) : std::vector<std::uint8_t>(generator() % 512);
            for (auto& byte : p) if (generator() % 3 == 0) byte = static_cast<std::uint8_t>(generator());
            const auto before = p; const auto forwarded = processor.process(p, Clock::now());
            require(forwarded.data() == p.data() && forwarded.size() == p.size() && p == before && processor.flows() <= 16);
        }
        require(processor.counters().packets == 30000);
    } else if (name == "options") {
        const std::string_view idle[]{"--mode", "idle", "--parent-pid", "1", "--stdio-control"}; require(parse_options(idle).filter() == "false");
        const std::string_view loop[]{"--mode", "loopback", "--port", "52000", "--check"}; require(parse_options(loop).filter().find("!impostor") != std::string::npos);
        const std::vector<std::vector<std::string_view>> bad{{}, {"--mode", "all", "--check"}, {"--mode", "idle"}, {"--mode", "loopback", "--port", "443", "--check"}, {"--mode", "idle", "--check", "--filter", "true"}, {"--version", "--check"}, {"--mode", "idle", "--check", "--check"}, {"--mode", "idle", "--port", "52000", "--check"}};
        for (const auto& args : bad) { bool rejected = false; try { (void)parse_options(args); } catch (const std::invalid_argument&) { rejected = true; } require(rejected); }
    } else throw std::runtime_error("Unknown test");
}
}
int main(int argc, char** argv) {
    try { if (argc != 2) throw std::runtime_error("Test group required"); check(argv[1]); std::cout << argv[1] << " passed\n"; return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
