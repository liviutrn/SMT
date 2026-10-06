#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <array>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#pragma comment(lib,"bcrypt.lib")
namespace fs=std::filesystem;

static constexpr std::array<unsigned char,32> STOCK={0x25,0x81,0x41,0x9a,0x64,0x23,0x7e,0x00,0xc6,0x68,0x81,0xa9,0xff,0x89,0xa4,0x0b,0x7a,0xb6,0xcf,0xd3,0xb7,0xde,0xeb,0xe7,0x38,0xb6,0x8d,0x66,0xcb,0x52,0x3f,0x6f};
static constexpr std::array<unsigned char,32> CENTER={0xf7,0xf3,0xf3,0xd8,0x64,0xcf,0x21,0xd3,0x5c,0x15,0x1d,0xaa,0x8a,0xbe,0xee,0xf3,0x6b,0xeb,0x29,0x34,0xbd,0x8c,0x8f,0x53,0x73,0x33,0xe3,0xf7,0x9d,0x26,0x67,0x2b};
static constexpr std::array<unsigned char,32> MEAN={0xdf,0x4c,0xd6,0xc1,0x44,0x2a,0xfa,0xa2,0xbf,0x6a,0x25,0x59,0xb2,0xb1,0x8d,0x6d,0x0a,0x3d,0x2e,0x88,0xe8,0x27,0x38,0x77,0xdb,0xef,0xd5,0x25,0x42,0x2e,0xf0,0x61};

static constexpr std::uint64_t OFF_CENTER_X=0x7377C;
static constexpr std::uint64_t OFF_CENTER_Y=0x73790;
static constexpr std::uint64_t OFF_MEAN_OP =0x73728;

enum class State { Stock,Center,Mean,Unknown,Missing };

bool running(const wchar_t* name){
 HANDLE s=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0); if(s==INVALID_HANDLE_VALUE)return false;
 PROCESSENTRY32W p{};p.dwSize=sizeof(p);bool f=false;
 if(Process32FirstW(s,&p))do{if(_wcsicmp(p.szExeFile,name)==0){f=true;break;}}while(Process32NextW(s,&p));
 CloseHandle(s);return f;
}
bool sha(const fs::path& p,std::array<unsigned char,32>& out){
 HANDLE f=CreateFileW(p.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
 if(f==INVALID_HANDLE_VALUE)return false;
 BCRYPT_ALG_HANDLE a=nullptr;BCRYPT_HASH_HANDLE h=nullptr;DWORD ol=0,hl=0,cb=0;std::vector<unsigned char> obj;bool ok=false;
 if(BCryptOpenAlgorithmProvider(&a,BCRYPT_SHA256_ALGORITHM,nullptr,0)!=0)goto end;
 if(BCryptGetProperty(a,BCRYPT_OBJECT_LENGTH,(PUCHAR)&ol,sizeof(ol),&cb,0)!=0)goto end;
 if(BCryptGetProperty(a,BCRYPT_HASH_LENGTH,(PUCHAR)&hl,sizeof(hl),&cb,0)!=0||hl!=32)goto end;
 obj.resize(ol);if(BCryptCreateHash(a,&h,obj.data(),ol,nullptr,0,0)!=0)goto end;
 {std::array<unsigned char,65536>b{};for(;;){DWORD n=0;if(!ReadFile(f,b.data(),(DWORD)b.size(),&n,nullptr))goto end;if(!n)break;if(BCryptHashData(h,b.data(),n,0)!=0)goto end;}}
 if(BCryptFinishHash(h,out.data(),32,0)!=0)goto end;ok=true;
end: if(h)BCryptDestroyHash(h);if(a)BCryptCloseAlgorithmProvider(a,0);CloseHandle(f);return ok;
}
State state(const fs::path& p){
 if(!fs::exists(p))return State::Missing;std::array<unsigned char,32>h{};if(!sha(p,h))return State::Unknown;
 if(h==STOCK)return State::Stock;if(h==CENTER)return State::Center;if(h==MEAN)return State::Mean;return State::Unknown;
}
const char* sname(State s){
 switch(s){case State::Stock:return "STOCK";case State::Center:return "CENTER-PASS";case State::Mean:return "PURE-MEAN";case State::Missing:return "MISSING";default:return "UNKNOWN/DIFFERENT";}
}
fs::path steamRoot(){
 HKEY k=nullptr;wchar_t v[4096]{};DWORD n=sizeof(v),t=0;
 if(RegOpenKeyExW(HKEY_CURRENT_USER,L"Software\\Valve\\Steam",0,KEY_READ,&k)!=ERROR_SUCCESS)return {};
 LONG r=RegQueryValueExW(k,L"SteamPath",nullptr,&t,(LPBYTE)v,&n);RegCloseKey(k);
 return r==ERROR_SUCCESS?fs::path(v):fs::path{};
}
fs::path findPrism(int argc,wchar_t**argv){
 for(int i=1;i<argc;i++){fs::path p=argv[i];if(fs::exists(p)&&_wcsicmp(p.filename().c_str(),L"prism.dll")==0)return p;}
 std::vector<fs::path> c={L"C:\\Games\\Steam\\steamapps\\common\\SteamVR\\bin\\win64\\prism.dll",L"C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR\\bin\\win64\\prism.dll"};
 auto r=steamRoot();if(!r.empty())c.push_back(r/L"steamapps/common/SteamVR/bin/win64/prism.dll");
 for(auto&p:c)if(fs::exists(p))return p;return {};
}
bool read32(const fs::path&p,std::uint64_t o,std::uint32_t&v){std::ifstream f(p,std::ios::binary);if(!f)return false;f.seekg((std::streamoff)o);f.read((char*)&v,4);return !!f;}
bool write32(std::fstream&f,std::uint64_t o,std::uint32_t v){f.seekp((std::streamoff)o);f.write((char*)&v,4);return !!f;}
bool backup(const fs::path&p,const fs::path&b,State cur){
 if(fs::exists(b))return state(b)==State::Stock;
 if(cur!=State::Stock)return false;std::error_code e;fs::copy_file(p,b,fs::copy_options::none,e);return !e&&state(b)==State::Stock;
}
bool stockbase(const fs::path&p,const fs::path&b,State cur){
 if(cur==State::Stock)return true;if(state(b)!=State::Stock)return false;std::error_code e;fs::copy_file(b,p,fs::copy_options::overwrite_existing,e);return !e&&state(p)==State::Stock;
}
bool centerPatch(const fs::path&p){
 std::uint32_t a=0,b=0;if(!read32(p,OFF_CENTER_X,a)||!read32(p,OFF_CENTER_Y,b)||a!=153||b!=153)return false;
 std::fstream f(p,std::ios::binary|std::ios::in|std::ios::out);if(!f)return false;
 if(!write32(f,OFF_CENTER_X,156)||!write32(f,OFF_CENTER_Y,156))return false;f.flush();f.close();return state(p)==State::Center;
}
bool meanPatch(const fs::path&p){
 std::uint32_t a=0;if(!read32(p,OFF_MEAN_OP,a)||a!=228)return false;
 std::fstream f(p,std::ios::binary|std::ios::in|std::ios::out);if(!f)return false;
 if(!write32(f,OFF_MEAN_OP,150))return false;f.flush();f.close();return state(p)==State::Mean;
}
int wmain(int argc,wchar_t**argv){
 std::puts("SteamVR Prism Vector Fix v3 - exact SteamVR 2.18.2 experiment");
 std::puts("Full 25/50/75 Motion Smoothing cadence is ALWAYS preserved.\n");
 fs::path p=findPrism(argc,argv);if(p.empty()){std::puts("prism.dll not found. Pass its full path as an argument.");return 1;}
 fs::path b=p.wstring()+L".prismfix-stock.bak";State cur=state(p);
 std::wprintf(L"File: %ls\n",p.c_str());std::printf("State: %s\n\n",sname(cur));
 std::puts("1 = CENTER-PASS [recommended first]");
 std::puts("    Disable MeanMaxBlur motion spreading; output existing center vector unchanged.");
 std::puts("2 = PURE-MEAN");
 std::puts("    Keep 3x3 average but remove largest-neighbor motion bias.");
 std::puts("3 = RESTORE STOCK");
 std::puts("4 = EXIT");
 std::printf("> ");
 int ch=0;if(std::scanf("%d",&ch)!=1||ch<1||ch>4)return 2;if(ch==4)return 0;
 if(running(L"vrcompositor.exe")||running(L"vrserver.exe")){std::puts("\nREFUSED: exit SteamVR completely first.");return 3;}
 if(ch==3){
   if(cur==State::Stock){std::puts("Already stock.");return 0;}
   if(state(b)!=State::Stock){std::puts("No valid stock backup. Use Steam Verify integrity.");return 4;}
   std::error_code e;fs::copy_file(b,p,fs::copy_options::overwrite_existing,e);
   if(e||state(p)!=State::Stock){std::puts("Restore failed.");return 5;}std::puts("Stock prism.dll restored.");return 0;
 }
 if(cur==State::Unknown||cur==State::Missing){std::puts("REFUSED: unsupported/different prism.dll.");return 6;}
 if(!backup(p,b,cur)){std::puts("Could not create/verify exact stock backup.");return 7;}
 if(!stockbase(p,b,cur)){std::puts("Could not restore stock base before patch.");return 8;}
 bool ok=ch==1?centerPatch(p):meanPatch(p);
 if(!ok){std::puts("Patch validation failed; restoring stock.");std::error_code e;fs::copy_file(b,p,fs::copy_options::overwrite_existing,e);return 9;}
 std::printf("\nInstalled: %s\n",sname(state(p)));
 std::puts("Start SteamVR normally. No cadence/timing/NVOFA changes were made.");
 return 0;
}
