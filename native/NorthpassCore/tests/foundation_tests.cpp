#include "northpass/transformation.hpp"
#include "northpass/reliability.hpp"
#include "northpass/flow.hpp"
#include <iostream>
#include <limits>
#include <ctime>
#include <stdexcept>
using namespace northpass;
namespace {
void require(bool b) { if (!b) throw std::runtime_error("Foundation assertion failed"); }
std::vector<std::uint8_t> udp(std::size_t length=36) {
    std::vector<std::uint8_t> p(length); p[0]=0x45; p[2]=static_cast<std::uint8_t>(length>>8); p[3]=static_cast<std::uint8_t>(length);
    p[8]=64; p[9]=17; p[20]=200; p[22]=201; p[24]=static_cast<std::uint8_t>((length-20)>>8); p[25]=static_cast<std::uint8_t>(length-20); return p;
}
void put_checksum(std::vector<std::uint8_t>& p, std::size_t at, std::span<const std::uint8_t> bytes) {
    std::uint32_t sum{};for(std::size_t i=0;i<bytes.size();i+=2)sum+=(static_cast<unsigned>(bytes[i])<<8)|(i+1<bytes.size()?bytes[i+1]:0);
    while(sum>>16)sum=(sum&65535)+(sum>>16);sum=(~sum)&65535;p[at]=static_cast<std::uint8_t>(sum>>8);p[at+1]=static_cast<std::uint8_t>(sum);
}
void run(std::string_view name) {
    auto bytes=udp(); TransformConfiguration c{3,TransformScope::Synthetic,TransformCapability::SyntheticByteReplacement,1500};
    if (name=="transformation") {
        PacketTransaction t(bytes,c); bytes[28]=99; require(t.original()[28]==0);
        require(t.propose({3,{{28,{42,43}}}}) && !t.committed() && t.result()[28]==0);
        require(t.commit() && t.result()[28]==42 && t.original()[28]==0); t.rollback(); require(t.result()[28]==0);
        c.scope=TransformScope::Live; c.capabilities=TransformCapability::None; PacketTransaction live(udp(),c);
        require(!live.propose({3,{{28,{42}}}}) && live.result()[28]==0);
    } else if (name=="rollback") {
        PacketTransaction t(bytes,c);
        for (const auto& p: {TransformProposal{2,{{28,{42}}}}, {3,{{36,{42}}}}, {3,{{std::numeric_limits<std::size_t>::max(),{42}}}},
             {3,{{28,{42,43}},{29,{44}}}}, {3,{{2,{0,1}}}}, {3,{{28,{}}}}}) {
            require(t.propose({3,{{28,{7}}}}) && t.commit()); require(!t.propose(p) && !t.committed());
            require(std::equal(t.result().begin(),t.result().end(),bytes.begin()));
        }
    } else if (name=="lifecycle") {
        struct StrategyTest:TransformStrategy {
            int initialized{},stopped{}; bool failure{};
            void initialize(const TransformConfiguration&) override { ++initialized; }
            TransformProposal propose(std::span<const std::uint8_t>) override { if(failure) throw std::bad_alloc(); return {3,{{28,{7}}}}; }
            void shutdown() noexcept override { ++stopped; }
        } s;
        require(evaluate_synthetic(s,bytes,c)[28]==7 && s.initialized==1 && s.stopped==1);
        s.failure=true; require(evaluate_synthetic(s,bytes,c)==bytes && s.stopped==2);
        c.version=99; require(evaluate_synthetic(s,bytes,c)==bytes);
    } else if (name=="checksums") {
        PacketMetadata m{true,true,false,false,false,false,false};
        require(observe_checksum(bytes,m)==ChecksumObservation::OffloadUnverified);
        m.ip_checksum=true; require(observe_checksum(bytes,m)==ChecksumObservation::Invalid);
        put_checksum(bytes,10,std::span(bytes).first(20));m.udp_checksum=true;
        std::vector<std::uint8_t> pseudo(bytes.begin()+12,bytes.begin()+20);pseudo.insert(pseudo.end(),{0,17,0,16});pseudo.insert(pseudo.end(),bytes.begin()+20,bytes.end());
        put_checksum(bytes,26,pseudo);require(observe_checksum(bytes,m)==ChecksumObservation::Valid);
        bytes[28]^=1;require(observe_checksum(bytes,m)==ChecksumObservation::Invalid);bytes[28]^=1;
        m.udp_checksum=false;require(observe_checksum(bytes,m)==ChecksumObservation::OffloadUnverified);m.udp_checksum=true;
        std::vector<std::uint8_t> v6(56);v6[0]=0x60;v6[5]=16;v6[6]=17;v6[7]=64;v6[44]=0;v6[45]=16;
        auto pseudo6=std::vector<std::uint8_t>(v6.begin()+8,v6.begin()+40);pseudo6.insert(pseudo6.end(),{0,0,0,16,0,0,0,17});pseudo6.insert(pseudo6.end(),v6.begin()+40,v6.end());
        put_checksum(v6,46,pseudo6);m.ipv6=true;require(observe_checksum(v6,m)==ChecksumObservation::Valid);
        v6[48]^=1;require(observe_checksum(v6,m)==ChecksumObservation::Invalid);
        m.ipv6=true; require(!metadata_consistent(bytes,m)); m.ipv6=false; m.impostor=true; require(!metadata_consistent(bytes,m));
    } else if (name=="mtu") {
        for (auto size : {std::size_t(68),std::size_t(1500),std::size_t(65535)}) {
            auto p=udp(size); c.mtu=size; PacketTransaction t(p,c); require(t.propose({}) && t.commit() && t.result().size()==size);
        }
        bool rejected=false; try { c.mtu=1500; PacketTransaction t(udp(1501),c); } catch(const std::invalid_argument&) { rejected=true; } require(rejected);
    } else if (name=="fault_scope") {
        require(parse_test_fault("driver-open",false,true)==TestFault::DriverOpen);
        for(auto fault:{"send","observation-delay","crash","unknown"}) { bool rejected=false;try{parse_test_fault(fault,false,true);}catch(const std::invalid_argument&){rejected=true;}require(rejected); }
        bool denied=false;try{parse_test_fault("send",true,false);}catch(const std::invalid_argument&){denied=true;}require(denied);
    } else if (name=="benchmark") {
        PassThroughStrategy s; PacketProcessor p(s); const auto start=Clock::now();const auto cpu=std::clock();
        for(int i=0;i<200000;++i) { auto v=bytes; v[20]=static_cast<std::uint8_t>(i>>8);v[21]=static_cast<std::uint8_t>(i); p.process(v,start); }
        const auto seconds=std::chrono::duration<double>(Clock::now()-start).count();require(seconds<20 && p.flows()<=4096);
        std::cout<<"SYNTHETIC benchmark packets=200000 seconds="<<seconds<<" packets_per_second="<<200000/seconds<<" cpu_seconds="<<static_cast<double>(std::clock()-cpu)/CLOCKS_PER_SEC<<" bounded_queue_storage_bytes="<<8*sizeof(QueuedPacket)<<" tracked="<<p.flows()<<" flow_capacity=4096; no driver/hardware measurement\n";
    } else throw std::runtime_error("Unknown case");
}
}
int main(int argc,char**argv){try{if(argc!=2)throw std::runtime_error("Group required");run(argv[1]);return 0;}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
