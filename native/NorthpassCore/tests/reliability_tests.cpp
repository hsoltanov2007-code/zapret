#include "northpass/flow.hpp"
#include "northpass/queue.hpp"
#include "northpass/metrics.hpp"
#include "northpass/ipc_protocol.hpp"
#include <algorithm>
#include <atomic>
#include <iostream>
#include <random>
#include <thread>
#include <vector>
using namespace northpass;
namespace {
void require(bool value) { if (!value) throw std::runtime_error("Reliability assertion failed"); }
void p16(std::vector<std::uint8_t>& b, std::size_t i, unsigned n) { b[i] = static_cast<std::uint8_t>(n >> 8); b[i+1] = static_cast<std::uint8_t>(n); }
std::vector<std::uint8_t> udp(bool v6 = false) {
    std::vector<std::uint8_t> b(v6 ? 56 : 36); const std::size_t ip = v6 ? 40 : 20;
    b[0] = v6 ? 0x60 : 0x45;
    if (v6) { p16(b,4,16); b[6]=17; b[7]=64; b[23]=1; b[39]=2; }
    else { p16(b,2,36); b[8]=64; b[9]=17; b[15]=1; b[19]=2; }
    p16(b,ip,52000); p16(b,ip+2,52001); p16(b,ip+4,16); return b;
}
void check(std::string_view name) {
    if (name == "validation") {
        auto b = udp(); require(classify(b).state == ParseState::Parsed);
        b[8]=0; require(classify(b).state == ParseState::Malformed); b[8]=64;
        b.push_back(0); require(classify(b).state == ParseState::Malformed); b.pop_back();
        b.insert(b.begin()+20,4,0); b[0]=0x46; p16(b,2,40); b[20]=7; b[21]=5; require(classify(b).state == ParseState::Malformed);
        b[21]=4; require(classify(b).state == ParseState::Parsed);
        std::vector<std::uint8_t> tcp(44); tcp[0]=0x45; p16(tcp,2,44); tcp[8]=64; tcp[9]=6; tcp[32]=0x60;
        tcp[40]=2; tcp[41]=4; require(classify(tcp).state==ParseState::Parsed);
        tcp[41]=5; require(classify(tcp).state==ParseState::Malformed); tcp[41]=4;
        tcp[32]=0x62; require(classify(tcp).state==ParseState::Malformed);
        auto v6=udp(true); v6.insert(v6.begin()+40,8,0); p16(v6,4,24); v6[6]=0; v6[40]=17; v6[42]=5; v6[43]=9;
        require(classify(v6).state == ParseState::Malformed); v6[43]=2; require(classify(v6).state == ParseState::Parsed);
        std::mt19937 random(123); PassThroughStrategy s; PacketProcessor processor(s);
        for (int i=0;i<20000;++i) { auto p=udp(i%2==0); for (auto& byte:p) if (random()%4==0) byte=static_cast<std::uint8_t>(random());
            const auto original=p; const auto result=processor.process(p,Clock::now()); require(result.data()==p.data() && p==original); }
    } else if (name == "fragment_details") {
        auto b=udp(); p16(b,4,123); p16(b,6,0x2001); auto v=classify(b);
        require(v.state==ParseState::Fragment && v.fragment_offset==8 && v.more_fragments && v.fragment_id==123 && v.payload.empty());
        p16(b,6,0x6000); require(classify(b).state==ParseState::Malformed); p16(b,6,0x8000); require(classify(b).state==ParseState::Malformed);
        auto v6=udp(true); v6.insert(v6.begin()+40,8,0); p16(v6,4,24); v6[6]=44; v6[40]=17; v6[47]=44;
        v=classify(v6); require(v.state==ParseState::Parsed && v.atomic_fragment && v.extension_count==1);
        v6[43]=9; v=classify(v6); require(v.state==ParseState::Fragment && v.fragment_offset==8 && v.more_fragments && v.fragment_id==44);
        v6[42]=0; v6[43]=2; require(classify(v6).state==ParseState::Malformed);
        v6[43]=1; v6.pop_back(); p16(v6,4,23); require(classify(v6).state==ParseState::Malformed);
        auto chain=udp(true); chain.insert(chain.begin()+40,8,0); chain[6]=60; chain[40]=0; p16(chain,4,24);
        require(classify(chain).state==ParseState::Malformed); // hop-by-hop cannot follow destination
    } else if (name == "retransmissions") {
        FlowTracker t; auto now=Clock::now(); PacketView v; v.state=ParseState::Parsed; v.ip_version=4; v.transport=Transport::Tcp;
        v.source.port=52000; v.destination.port=52001; v.tcp_flags=2; v.sequence=0xfffffff0;
        require(t.observe(v,40,now)->tcp==TcpObservation::Syn);
        require(t.observe(v,40,now)->retransmissions==1);
        std::swap(v.source,v.destination); v.tcp_flags=0x12; v.sequence=900; v.acknowledgement=0xfffffff1;
        require(t.observe(v,40,now)->tcp==TcpObservation::SynAck);
        v.tcp_flags=0x10; require(t.observe(v,40,now)->tcp==TcpObservation::SynAck);
        std::swap(v.source,v.destination); v.sequence=0xfffffff1; v.acknowledgement=901;
        require(t.observe(v,40,now)->tcp==TcpObservation::Established);
        v.tcp_flags=2; v.sequence=0xfffffff0; require(t.observe(v,40,now)->tcp==TcpObservation::Established);
        v.tcp_flags=0x10; v.sequence=0xfffffff1;
        std::array<std::uint8_t,32> data{}; v.payload=data;
        require(t.observe(v,72,now)->next_sequence[0]==17);
        require(t.observe(v,72,now)->retransmissions==3);
        v.sequence=40; require(t.observe(v,72,now)->out_of_order==1);
        v.payload={}; v.sequence=72; const auto* f=t.observe(v,40,now); require(f->duplicate_acks>=1);
        v.tcp_flags=1; require(t.observe(v,40,now)->tcp==TcpObservation::Closing);
        v.tcp_flags=0x10; require(t.observe(v,40,now)->tcp==TcpObservation::Closing);
        v.tcp_flags=4; require(t.observe(v,40,now)->tcp==TcpObservation::Reset);
        v.tcp_flags=2; v.sequence=10; require(t.observe(v,40,now)->retransmissions==0 && t.size()==1);
    } else if (name == "flow_expiry") {
        FlowTracker t(2); auto bytes=udp(); auto v=classify(bytes); auto now=Clock::now();
        t.observe(v,36,now); t.expire(now+std::chrono::seconds(29)); require(t.size()==1);
        t.expire(now+std::chrono::seconds(30)); require(t.size()==0);
        t.observe(v,36,now); v.source.port++; t.observe(v,36,now); v.source.port++; t.observe(v,36,now);
        require(t.evictions()==1 && t.size()==2);
        v.transport=Transport::Tcp; v.tcp_flags=4; t.observe(v,40,now); t.expire(now+std::chrono::seconds(5)); require(t.size()==1);
    } else if (name == "strategies") {
        validate_strategy({});
        for (auto d : {StrategyDefinition{1,"original",3,0}, {2,"desync",3,0}, {2,"original",7,0}, {2,"original",3,1}}) {
            bool rejected=false; try { validate_strategy(d); } catch (const std::invalid_argument&) { rejected=true; } require(rejected);
        }
    } else if (name == "recovery") {
        struct Failing : Strategy { StrategyAction inspect(const PacketView&,const FlowSummary*) override { throw std::bad_alloc(); } } strategy;
        PacketProcessor p(strategy); auto b=udp(); auto before=b; require(p.process(b,Clock::now()).data()==b.data() && b==before);
        require(p.counters().recoverable_errors==1 && p.counters().rollbacks==1);
        struct InvalidAction : Strategy { StrategyAction inspect(const PacketView&,const FlowSummary*) override { return static_cast<StrategyAction>(99); } } invalid;
        PacketProcessor rollback(invalid); require(rollback.process(b,Clock::now()).data()==b.data() && rollback.counters().rollbacks==1);
    } else if (name == "queue") {
        PacketQueue q(1); QueuedPacket p; p.length=3; p.bytes[0]=7; p.metadata[0]=9;
        require(q.try_push(p) && !q.try_push(p) && q.peak()==1); q.close(); require(!q.try_push(p));
        QueuedPacket out; require(q.pop(out,std::chrono::milliseconds(1)) && out.bytes[0]==7 && out.metadata[0]==9 && out.length==3);
        require(q.drained() && !q.pop(out,std::chrono::milliseconds(1)));
    } else if (name == "concurrency") {
        PacketQueue q(8); std::atomic<std::uint64_t> admitted{}, consumed{}, bypassed{};
        std::thread consumer([&] { QueuedPacket p; while (!q.drained()) if (q.pop(p,std::chrono::milliseconds(1))) { require(p.length==16 && p.bytes[0]==42); ++consumed; } });
        std::vector<std::thread> producers;
        for (int n=0;n<4;++n) producers.emplace_back([&] { QueuedPacket p; p.length=16; p.bytes[0]=42; for (int i=0;i<20000;++i) { if(q.try_push(p)) ++admitted; else ++bypassed; } });
        for(auto& p:producers) p.join(); q.close(); consumer.join(); require(admitted==consumed && admitted+bypassed==80000 && q.peak()<=8);
        PacketQueue closed; std::thread blocked([&] { QueuedPacket p; require(!closed.pop(p,std::chrono::seconds(5))); });
        closed.close(); blocked.join(); require(closed.drained());
    } else if (name == "memory") {
        FlowTracker t(128); auto b=udp(); auto v=classify(b); auto now=Clock::now();
        for(int i=0;i<100000;++i) { v.source.port=static_cast<std::uint16_t>(i); t.observe(v,36,now); require(t.size()<=128); }
        require(t.evictions()>90000); PacketQueue q(64); require(q.capacity()*sizeof(QueuedPacket)<5*1024*1024);
    } else if (name == "performance") {
        PassThroughStrategy s; PacketProcessor p(s); auto b=udp(); const auto started=Clock::now();
        for(int i=0;i<100000;++i) p.process(b,started);
        const auto elapsed=Clock::now()-started; require(p.counters().packets==100000 && elapsed<std::chrono::seconds(20));
        std::cout << "Synthetic 100k packet observation ms=" << std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count() << '\n';
    } else if (name == "metrics") {
        Metrics m; std::vector<std::thread> threads;
        for(int n=0;n<4;++n) threads.emplace_back([&] { for(int i=0;i<10000;++i) { ++m.captured; ++m.forwarded; m.record_latency(1000); } });
        for(auto& t:threads) t.join(); const auto text=m.line(2.5,123456);
        require(m.captured==40000 && m.measured==40000 && m.latency_max_ns==1000 && text.find("kernel_loss_unknown=1")!=std::string::npos && text.find("latency_us=1.000")!=std::string::npos);
    } else if (name == "ipc_codec") {
        const std::string nonce(64,'a'); require(authenticate("AUTH 2 "+nonce,nonce)); require(!authenticate("AUTH 2 "+std::string(64,'b'),nonce));
        require(!authenticate("AUTH 1 "+nonce,nonce) && !valid_pipe_id("../other") && valid_pipe_id(std::string(32,'b')));
        require(parse_command("PING 2 1",1).kind==CommandKind::Ping); require(parse_command("STOP 2 2",2).sequence==2);
        for(auto text : {"STOP 2 1","STOP 2 02","FILTER 2 2","METRICS 1 2","STOP 2 -2","STOP 2 2 x","STOP 2 4294967296"}) {
            bool rejected=false; try { (void)parse_command(text,2); } catch(const std::invalid_argument&) { rejected=true; } require(rejected);
        }
    } else throw std::runtime_error("Unknown reliability group");
}
}
int main(int argc,char** argv) { try { if(argc!=2) throw std::runtime_error("Group required"); check(argv[1]); std::cout<<argv[1]<<" passed\n"; return 0; }
catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; } }
