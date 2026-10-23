// Original, deliberately small process boundary around Manifold 3.5.4.
// The worker has no document, undo history, UI, or user-file write capability in its protocol.
#ifdef _MSC_VER
#pragma warning(push)
// Manifold's public math.h narrows a bit-hash internally; keep /WX on our adapter.
#pragma warning(disable:4244)
#endif
#include <manifold/manifold.h>
#ifdef _MSC_VER
#pragma warning(pop)
#endif
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>
#include <optional>
#include "SimplifySafety.h"
#include "SmoothSafety.h"
#include "SeparateSafety.h"
#include "ExtrudeDown.h"

using manifold::Manifold;
constexpr int32_t magic = 0x42574b31, version = 8, maxVertices = 300000, maxTriangles = 100000;
template<class T> T read(std::istream& in) {
  T value{};
  if (!in.read(reinterpret_cast<char*>(&value), sizeof(T))) throw std::runtime_error("Incomplete geometry request.");
  return value;
}
template<class T> void write(std::ostream& out, T value) {
  out.write(reinterpret_cast<const char*>(&value), sizeof(T));
  if (!out) throw std::runtime_error("Could not write geometry result.");
}
double coordinate(std::istream& in) {
  double v = read<double>(in);
  if (!std::isfinite(v) || std::abs(v) > 10000000) throw std::runtime_error("Invalid coordinate.");
  return v;
}
Manifold readMesh(std::istream& in,manifold::MeshGL64* raw=nullptr) {
  int32_t nv = read<int32_t>(in), nt = read<int32_t>(in);
  if (nv < 1 || nv > maxVertices || nt < 1 || nt > maxTriangles) throw std::runtime_error("Input exceeds geometry limits.");
  manifold::MeshGL64 mesh;
  mesh.vertProperties.reserve(size_t(nv) * 3);
  for (int64_t i = 0; i < int64_t(nv) * 3; ++i) mesh.vertProperties.push_back(coordinate(in));
  mesh.triVerts.reserve(size_t(nt) * 3);
  for (int64_t i = 0; i < int64_t(nt) * 3; ++i) {
    int32_t index = read<int32_t>(in);
    if (index < 0 || index >= nv) throw std::runtime_error("Invalid triangle index.");
    mesh.triVerts.push_back(static_cast<uint64_t>(index));
  }
  if(raw)*raw=mesh;
  Manifold solid(mesh);
  if (solid.Status() != Manifold::Error::NoError)
    throw std::domain_error("The input must be a closed, consistently oriented solid. Repair it before using this tool.");
  if (solid.IsEmpty() || solid.Volume() <= 0)
    throw std::domain_error("The input has no positive solid volume. Check its face orientation.");
  return solid;
}
void writeMesh(std::ostream& out, const Manifold& solid) {
  if (solid.Status() != Manifold::Error::NoError) throw std::domain_error("The geometry calculation failed. The original objects are unchanged.");
  // A face-only contact may have triangles but exactly zero volume. It is an empty solid,
  // not a printable sheet. Do not use an epsilon that could discard a thin positive-volume part.
  if (solid.IsEmpty() || solid.Volume() == 0) { write(out,int32_t(0)); write(out,int32_t(0)); return; }
  if (solid.Volume() < 0) throw std::domain_error("The geometry result has invalid orientation.");
  if (solid.NumTri() > maxTriangles || solid.NumPropVert() > maxVertices)
    throw std::domain_error("The result exceeds the prototype's geometry limits.");
  const auto mesh = solid.GetMeshGL64();
  write(out,static_cast<int32_t>(mesh.NumVert())); write(out,static_cast<int32_t>(mesh.NumTri()));
  for (uint64_t i = 0; i < mesh.NumVert(); ++i)
    for (uint64_t j = 0; j < 3; ++j) write(out,mesh.vertProperties[size_t(i * mesh.numProp + j)]);
  // Resolve property-vertex aliases so touching shells retain the kernel's explicit topology.
  std::vector<uint64_t> alias(mesh.NumVert());
  for (uint64_t i = 0; i < alias.size(); ++i) alias[size_t(i)] = i;
  for (size_t i = 0; i < mesh.mergeFromVert.size(); ++i) alias[size_t(mesh.mergeFromVert[i])] = mesh.mergeToVert[i];
  for (auto index : mesh.triVerts) {
    size_t hops = 0;
    while (alias[size_t(index)] != index) {
      index = alias[size_t(index)];
      if (++hops > alias.size()) throw std::runtime_error("Invalid vertex aliases.");
    }
    write(out,static_cast<int32_t>(index));
  }
}
// Inputs have already been normalized/validated by Core. Keep protocol bounds and
// independent positive-area/volume checks here; the kernel is not a path repairer.
double polygonArea(const manifold::SimplePolygon& polygon) {
  double twice = 0;
  const auto origin = polygon.front();
  for (size_t i=0; i<polygon.size(); ++i) {
    const auto a=polygon[i]-origin, b=polygon[(i+1)%polygon.size()]-origin;
    twice += a.x*b.y-a.y*b.x;
  }
  return twice/2;
}
bool contains(const manifold::SimplePolygon& polygon, manifold::vec2 point) {
  bool inside=false;
  for (size_t i=0; i<polygon.size(); ++i) {
    const auto a=polygon[i], b=polygon[(i+1)%polygon.size()];
    if ((a.y>point.y)!=(b.y>point.y) && point.x<(b.x-a.x)*(point.y-a.y)/(b.y-a.y)+a.x) inside=!inside;
  }
  return inside;
}
bool nearVolume(double actual, double expected) {
  // No absolute floor: a small disconnected dot must not be masked by a large glyph.
  return actual>0 && expected>0 && std::isfinite(actual) && std::isfinite(expected) && std::abs(actual-expected)<=expected*1e-6;
}
Manifold textSolid(std::istream& in, int32_t op, const Manifold& target) {
  const auto depth=read<double>(in), overlap=read<double>(in);
  if (!std::isfinite(depth) || depth<0.05 || depth>1000 || !std::isfinite(overlap) ||
      (op==5 ? overlap!=0 : overlap!=std::min(0.1,depth/10))) throw std::runtime_error("Invalid text depth/overlap.");
  manifold::mat3x4 placement;
  for (int c=0;c<4;++c) for (int r=0;r<3;++r) placement[c][r]=coordinate(in);
  const int32_t count=read<int32_t>(in);
  if (count<1 || count>256) throw std::runtime_error("Invalid text contour count.");
  manifold::Polygons polygons;
  std::vector<double> areas;
  int32_t total=0;
  for (int32_t i=0;i<count;++i) {
    const int32_t points=read<int32_t>(in); total+=points;
    if (points<3 || total>4096) throw std::runtime_error("Invalid text contour points.");
    manifold::SimplePolygon polygon;
    for (int32_t p=0;p<points;++p) { const auto x=coordinate(in), y=coordinate(in); polygon.push_back({x,y}); }
    const double area=polygonArea(polygon);
    if (!std::isfinite(area) || area==0) throw std::domain_error("Text contains unusable contour area.");
    areas.push_back(area); polygons.push_back(std::move(polygon));
  }
  // Assign each hole to its nearest positive outer. A nested filled island is a
  // separate support region, so its missing support can never hide in aggregate volume.
  std::vector<manifold::Polygons> regions;
  std::vector<int32_t> outerIndices;
  std::vector<double> regionAreas;
  for (int32_t i=0;i<count;++i) if (areas[size_t(i)]>0) {
    regions.push_back({polygons[size_t(i)]}); outerIndices.push_back(i); regionAreas.push_back(areas[size_t(i)]);
  }
  for (int32_t i=0;i<count;++i) if (areas[size_t(i)]<0) {
    int owner=-1; double ownerArea=INFINITY;
    for (size_t j=0;j<outerIndices.size();++j) {
      const auto oi=size_t(outerIndices[j]);
      if (areas[oi]<ownerArea && contains(polygons[oi],polygons[size_t(i)].front())) {owner=static_cast<int>(j);ownerArea=areas[oi];}
    }
    if (owner<0) throw std::domain_error("A text hole has no filled outer contour.");
    regions[size_t(owner)].push_back(polygons[size_t(i)]);regionAreas[size_t(owner)]+=areas[size_t(i)];
  }
  double area=0;
  for (size_t i=0;i<regions.size();++i) {
    if (regionAreas[i]<=0) throw std::domain_error("Text has no usable filled region.");
    area+=regionAreas[i];
    const auto inward=op==7?depth+overlap:op==6?overlap:depth;
    auto region=Manifold::Extrude(regions[i],inward);
    if (!nearVolume(region.Volume(),regionAreas[i]*inward)) throw std::domain_error("Text features are below reliable solid precision.");
    if (op!=5) {
      region=region.Translate({0,0,-inward}).Transform(placement);
      const auto supported=target.Boolean(region,manifold::OpType::Intersect);
      if (supported.Status()!=Manifold::Error::NoError || !nearVolume(supported.Volume(),regionAreas[i]*inward))
        throw std::domain_error(op==7 ? "Blind engraving requires full material support and positive backing beneath every filled region." :
          "Raised text requires a planar supporting face beneath every filled region; floating or partial labels are unsupported.");
    }
  }
  const auto bottom=op==5?0:op==6?-overlap:-depth;
  const auto height=op==5?depth:depth+overlap;
  auto tool=Manifold::Extrude(polygons,height).Translate({0,0,bottom}).Transform(placement);
  if (tool.Status()!=Manifold::Error::NoError || !nearVolume(tool.Volume(),area*height))
    throw std::domain_error("Text features are below reliable solid precision.");
  if (op==5) return tool;
  auto result=target.Boolean(tool,op==6?manifold::OpType::Add:manifold::OpType::Subtract);
  const auto delta=op==6?result.Volume()-target.Volume():target.Volume()-result.Volume();
  if (result.Status()!=Manifold::Error::NoError || result.IsEmpty() || !nearVolume(delta,area*depth))
    throw std::domain_error("Emboss must produce the full requested raised height or blind depth on a planar face.");
  return result;
}
int run(const std::filesystem::path& input, const std::filesystem::path& output) {
  int32_t status = 0;
  std::string message;
  std::vector<Manifold> results;
  int32_t op=-1, cavityCount=0;double maximumKernelRadius=0;
  std::optional<simplify_safety::Result> simplification;
  std::optional<smooth_safety::Result> smoothing;
  std::optional<std::vector<separate_safety::Piece>> separation;
  std::optional<extrude_down::Result> extrusion;
  try {
    if (std::filesystem::file_size(input) > 64 * 1024 * 1024) throw std::runtime_error("Geometry request too large.");
    std::ifstream in(input,std::ios::binary);
    if (read<int32_t>(in) != magic || read<int32_t>(in) != version) throw std::runtime_error("Geometry protocol version mismatch.");
    op = read<int32_t>(in);
    double nx = coordinate(in), ny = coordinate(in), nz = coordinate(in), offset = coordinate(in);
    int32_t count = read<int32_t>(in);
    if (op < 0 || op > 12 || count != (op==5?0:op>=3?1:2)) throw std::runtime_error("Invalid geometry operation.");
    manifold::MeshGL64 raw;auto a = count==0?Manifold():readMesh(in,op>=9?&raw:nullptr);
    if (op == 3) {
      double length = std::hypot(nx,ny,nz);
      if (length == 0) throw std::runtime_error("The plane normal cannot be zero.");
      auto halves = a.SplitByPlane({nx/length,ny/length,nz/length},offset);
      results = {halves.first,halves.second};
    } else if (op == 4) {
      results = {a}; // Validation only. The caller retains its original candidate mesh.
    } else if (op==12) {
      extrusion=extrude_down::calculate(a,raw,read<double>(in));results={extrusion->solid};
    } else if (op==11) {
      separation=separate_safety::separate(raw);
    } else if (op==10) {
      const auto strength=read<double>(in),maximum=read<double>(in);const auto protect=read<int32_t>(in);
      if(protect!=0&&protect!=1)throw std::runtime_error("Invalid sharp-edge protection flag.");
      smoothing=smooth_safety::smooth(raw,strength,maximum,protect==1);
    } else if (op==9) {
      const auto reduction=read<double>(in),maximum=read<double>(in);
      simplification=simplify_safety::simplify(a,raw,reduction,maximum);results={simplification->solid};
    } else if (op==8) {
      const auto wall=read<double>(in);
      if (!std::isfinite(wall)||wall<0.05||wall>1000) throw std::domain_error("Wall thickness must be from 0.05 to 1,000 mm.");
      if (a.NumTri()>4096) throw std::domain_error("Hollow supports at most 4,096 operand triangles in this prototype.");
      if (a.Decompose().size()!=1) throw std::domain_error("Hollow requires one connected outer surface. Already-hollow or disconnected shells are unsupported.");
      const auto unit=Manifold::Sphere(1,24);const auto mesh=unit.GetMeshGL64();
      auto point=[&](uint64_t i){return manifold::vec3(mesh.vertProperties[size_t(i*mesh.numProp)],mesh.vertProperties[size_t(i*mesh.numProp+1)],mesh.vertProperties[size_t(i*mesh.numProp+2)]);};
      double inradius=1,maxradius=0;
      for(uint64_t i=0;i<mesh.NumVert();++i) maxradius=std::max(maxradius,linalg::length(point(i)));
      for(size_t i=0;i<mesh.triVerts.size();i+=3){
        const auto p=point(mesh.triVerts[i]),q=point(mesh.triVerts[i+1]),r=point(mesh.triVerts[i+2]);const auto normal=linalg::cross(q-p,r-p);
        const auto plane=linalg::dot(normal,p)/linalg::length(normal);
        if(!std::isfinite(plane)||plane<=0)throw std::runtime_error("Invalid hollow offset kernel.");
        inradius=std::min(inradius,plane);
      }
      const auto scale=wall/inradius*(1+1e-10);maximumKernelRadius=maxradius*scale;
      if(maximumKernelRadius>wall*1.03)throw std::runtime_error("Hollow offset kernel exceeds its approximation bound.");
      const auto kernel=unit.Scale({scale,scale,scale});const auto cavity=a.MinkowskiDifference(kernel);
      if(cavity.Status()!=Manifold::Error::NoError)throw std::domain_error("Could not calculate a valid hollow cavity.");
      if(cavity.IsEmpty()||cavity.Volume()<=0)throw std::domain_error("The requested wall leaves no hollow cavity. Try a thinner wall.");
      const auto parts=cavity.Decompose();cavityCount=static_cast<int32_t>(parts.size());
      for(const auto& part:parts)if(part.Volume()<=0)throw std::domain_error("Hollow cavity orientation is invalid.");
      const auto outside=cavity.Boolean(a,manifold::OpType::Subtract);
      if(outside.Status()!=Manifold::Error::NoError||(!outside.IsEmpty()&&outside.Volume()!=0)||cavity.Volume()>=a.Volume())
        throw std::domain_error("The hollow cavity is not contained in the original solid.");
      if(cavity.NumTri()>maxTriangles||cavity.NumPropVert()>maxVertices)throw std::domain_error("The hollow cavity exceeds geometry limits.");
      results={cavity};
    } else if (op>=5) {
      results={textSolid(in,op,a)};
    } else {
      auto b = readMesh(in);
      results = {a.Boolean(b,op == 0 ? manifold::OpType::Add : op == 1 ? manifold::OpType::Subtract : manifold::OpType::Intersect)};
    }
    if (in.peek() != std::char_traits<char>::eof()) throw std::runtime_error("Unexpected geometry request data.");
    for (const auto& result : results)
      if (result.Status() != Manifold::Error::NoError) throw std::domain_error("The geometry calculation failed. Check the input solids.");
  } catch (const std::domain_error& e) { status = 1; message = e.what(); }
    catch (const std::exception& e) { status = 2; message = e.what(); }
  std::ofstream out(output,std::ios::binary | std::ios::trunc);
  write(out,magic); write(out,version); write(out,status);
  write(out,static_cast<int32_t>(message.size())); out.write(message.data(),static_cast<std::streamsize>(message.size()));
  write(out,status == 0 ? (smoothing?int32_t(1):static_cast<int32_t>(results.size())) : int32_t(0));
  if(status==0&&smoothing){const auto& d=smoothing->data;
    write(out,static_cast<int32_t>(d.v.size()));write(out,static_cast<int32_t>(d.t.size()));
    for(auto v:d.v){write(out,v.x);write(out,v.y);write(out,v.z);}for(const auto& t:d.t)for(auto v:t)write(out,static_cast<int32_t>(v));
  }else if(status==0)for(const auto& result:results)writeMesh(out,result);
  if(status==0&&op==8){write(out,maximumKernelRadius);write(out,cavityCount);}
  if(status==0&&op==9){const auto& s=*simplification;write(out,s.bound);write(out,s.boundaries);write(out,s.cavities);write(out,s.tolerance);write(out,s.beforeVolume);write(out,s.afterVolume);}
  if(status==0&&op==10){const auto& s=*smoothing;write(out,s.movement);write(out,s.bound);write(out,s.fixedVertices);write(out,s.clippedVertices);write(out,s.boundaries);write(out,s.cavities);write(out,s.beforeVolume);write(out,s.afterVolume);}
  if(status==0&&op==11){write(out,static_cast<int32_t>(separation->size()));for(const auto& p:*separation){write(out,static_cast<int32_t>(p.triangles.size()));write(out,p.boundaries);write(out,p.cavities);write(out,p.volume);for(auto index:p.triangles)write(out,index);}}
  if(status==0&&op==12){write(out,extrusion->before);write(out,extrusion->after);write(out,extrusion->added);}
  out.flush();
  return out ? 0 : 2;
}
#ifdef _WIN32
int wmain(int argc, wchar_t* argv[]) {
#else
int main(int argc, char* argv[]) {
#endif
  if (argc != 3) return 2;
  try { return run(argv[1],argv[2]); } catch (...) { return 2; }
}
