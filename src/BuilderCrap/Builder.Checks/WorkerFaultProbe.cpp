// Test-only peer for the worker process protocol. Never copied to the application output.
#include <cstdlib>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <manifold/manifold.h>
#include "../Builder.GeometryWorker/SimplifySafety.h"
static int safetyTests(){
  using namespace simplify_safety;
  Data valid{{V(.2,2.65,5.1),V(.2,5.1,7.55),V(.2,2.65,7.55),V(.2,7.55,7.55),V(.2,7.55,10)},{{0,1,2},{1,3,4}}};
  if(intersectsBeyondShared(valid,0,1,1e-8))return 31;
  Data crossing{{V(0,0,0),V(10,1e-10,0),V(0,1,0),V(0,1e-10,0),V(10,0,0),V(10,-1,0)},{{0,1,2},{3,4,5}}};
  if(!intersectsBeyondShared(crossing,0,1,1e-8))return 32;
  Data overlap{{V(0,0,0),V(4,0,0),V(0,4,0),V(3,1,0),V(3,3,0)},{{0,1,2},{0,3,4}}};
  if(!intersectsBeyondShared(overlap,0,1,1e-8))return 33;
  Data separate{{V(0,0,0),V(10,0,0),V(0,1,0),V(0,-.0001,0),V(10,-.0001,0),V(10,-1,0)},{{0,1,2},{3,4,5}}};
  if(intersectsBeyondShared(separate,0,1,1e-8))return 34;
  return 0;
}
int wmain(int argc, wchar_t* argv[]) {
  if(argc==2&&std::wstring(argv[1])==L"--simplify-safety-tests")return safetyTests();
  if(argc != 3) return 20;
  std::ifstream input(std::filesystem::path(argv[1]),std::ios::binary);
  int32_t operation = -1; input.seekg(8); input.read(reinterpret_cast<char*>(&operation),sizeof(operation));
  if(operation == 0 || operation == 7) std::_Exit(27); // Abrupt process failure, without destructors or output.
  if(operation == 1 || operation == 5) {
    std::ofstream output(std::filesystem::path(argv[2]),std::ios::binary);
    output << "truncated"; // Successful process, invalid response.
  }
  if(operation == 3 || operation == 8 || operation == 9 || operation == 10 || operation == 11 || operation == 12) {
    std::ofstream output(std::filesystem::path(argv[2]),std::ios::binary);
    int32_t header[] = {0x42574b31,2,0,0,0}; // Deliberately stale version after protocol3 extension.
    output.write(reinterpret_cast<const char*>(header),sizeof(header));
  }
  return 0; // Operations 2/6: successful process, missing response.
}
