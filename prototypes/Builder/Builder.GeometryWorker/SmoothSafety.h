// Original uniform Taubin filter and bounded material verification. The shared
// geometric predicates remain unchanged so simplification keeps its semantics.
#pragma once
#include "SimplifySafety.h"

namespace smooth_safety {
namespace safety=simplify_safety;
using safety::V;
struct Result {
  safety::Data data;
  double movement=0,bound=0,beforeVolume=0,afterVolume=0;
  int fixedVertices=0,clippedVertices=0,boundaries=0,cavities=0;
};
inline Result smooth(const manifold::MeshGL64& raw,double strength,double maximum,bool protect){
  if(!std::isfinite(strength)||strength<=0||strength>100||!std::isfinite(maximum)||maximum<=0||maximum>1000)
    throw std::domain_error("Choose smoothing strength greater than 0% and at most 100%, and a positive deviation at most 1,000 mm.");
  safety::Budget budget;safety::Surface source(raw);const auto margin=source.margin*8;
  if(maximum<=2*margin)throw std::domain_error("The requested smoothing limit is below the numerical verification margin at these coordinates.");
  safety::collisionNodes(*source.tree,0,0,margin,budget);
  const auto count=source.boundaries.size();if(count>1024)throw std::domain_error("Too many material boundaries for bounded smoothing verification.");
  std::vector<std::vector<bool>> nesting(count,std::vector<bool>(count,false));
  for(size_t i=0;i<count;++i)for(size_t j=0;j<count;++j)if(i!=j)
    nesting[i][j]=safety::inside(source.data.v[size_t(source.boundaries[i].vertices[0])],source,j,budget);
  for(size_t i=0;i<count;++i)if((source.boundaries[i].volume>0)!=(std::count(nesting[i].begin(),nesting[i].end(),true)%2==0))
    throw std::domain_error("Source shell orientation does not match its nesting. This model cannot be smoothed safely.");

  const auto& vertices=source.data.v;const auto& faces=source.data.t;
  std::vector<std::set<int>> neighbors(vertices.size());std::map<std::pair<int,int>,std::vector<int>> edgeFaces;
  std::vector<V> normals;normals.reserve(faces.size());
  for(size_t f=0;f<faces.size();++f){const auto& t=faces[f];const auto n=linalg::cross(vertices[size_t(t[1])]-vertices[size_t(t[0])],vertices[size_t(t[2])]-vertices[size_t(t[0])]);normals.push_back(n/linalg::length(n));
    for(int k=0;k<3;++k){const auto a=t[size_t(k)],b=t[size_t((k+1)%3)];neighbors[size_t(a)].insert(b);neighbors[size_t(b)].insert(a);edgeFaces[std::minmax(a,b)].push_back(static_cast<int>(f));}}
  std::vector<bool> fixed(vertices.size(),false);
  if(protect)for(const auto& [edge,incident]:edgeFaces){
    if(incident.size()!=2)throw std::domain_error("Smoothing needs closed manifold edges.");
    // Small angular comparison margin includes an original exact 60-degree edge.
    if(linalg::dot(normals[size_t(incident[0])],normals[size_t(incident[1])])<=.5+1e-12){fixed[size_t(edge.first)]=true;fixed[size_t(edge.second)]=true;}}
  auto filtered=vertices;
  for(int pair=0;pair<10;++pair)for(double coefficient:{.5,-.53}){
    auto next=filtered;
    for(size_t i=0;i<vertices.size();++i){
      if(fixed[i]){next[i]=vertices[i];continue;}
      V sum(0.0);for(auto n:neighbors[i])sum+=filtered[size_t(n)];
      if(neighbors[i].empty())throw std::domain_error("Smoothing cannot use an isolated vertex.");
      next[i]=filtered[i]+coefficient*(sum/static_cast<double>(neighbors[i].size())-filtered[i]);}
    filtered=std::move(next);
  }
  Result output;output.data=source.data;output.fixedVertices=static_cast<int>(std::count(fixed.begin(),fixed.end(),true));
  // Keep a second margin for the rounded world-coordinate addition. The
  // reported bound is recomputed from the actual returned doubles below.
  const auto movementLimit=maximum-2*margin;
  for(size_t i=0;i<vertices.size();++i){
    if(fixed[i])continue;
    auto displacement=(filtered[i]-vertices[i])*(strength/100);const auto length=linalg::length(displacement);
    if(!std::isfinite(length))throw std::domain_error("Smoothing produced a nonfinite displacement.");
    if(length>movementLimit){displacement*=movementLimit/length;++output.clippedVertices;}
    output.data.v[i]=vertices[i]+displacement;
    output.movement=std::max(output.movement,linalg::length(output.data.v[i]-vertices[i]));
  }
  if(output.movement<=margin)throw std::domain_error("These smoothing settings make no measurable surface change. Sharp-edge protection may fix every affected vertex.");
  output.bound=output.movement+margin;
  if(!std::isfinite(output.bound)||output.bound>maximum)throw std::domain_error("The smoothing displacement cannot be represented within the requested numerical limit.");

  manifold::MeshGL64 mesh;mesh.numProp=3;
  for(auto v:output.data.v){mesh.vertProperties.push_back(v.x);mesh.vertProperties.push_back(v.y);mesh.vertProperties.push_back(v.z);}
  for(const auto& t:output.data.t)for(auto v:t)mesh.triVerts.push_back(static_cast<uint64_t>(v));
  safety::Surface candidate(mesh);
  // The returned mesh is the authored indexed mesh below, never the kernel's
  // cleaned/reordered output. Reject any exact-weld collapse in that candidate.
  if(candidate.data.t!=source.data.t||candidate.data.v.size()!=vertices.size()||candidate.boundaries.size()!=count)
    throw std::domain_error("Smoothing would collapse vertices or alter indexed connectivity.");
  safety::collisionNodes(*candidate.tree,0,0,margin,budget);
  std::vector<double> bounds(count,margin);
  for(size_t i=0;i<count;++i){
    const auto& original=source.boundaries[i];const auto& result=candidate.boundaries[i];
    if(original.triangles!=result.triangles||original.euler!=result.euler||(original.volume>0)!=(result.volume>0))
      throw std::domain_error("Smoothing would alter a material boundary or its orientation.");
    for(auto v:original.vertices)bounds[i]=std::max(bounds[i],linalg::length(candidate.data.v[size_t(v)]-vertices[size_t(v)])+margin);
    output.beforeVolume+=original.volume;output.afterVolume+=result.volume;if(original.volume<0)++output.cavities;
  }
  // Each corresponding triangle uses the same barycentric coordinates, so the
  // largest matched vertex displacement bounds its whole surface both ways.
  for(size_t i=0;i<count;++i)for(size_t j=i+1;j<count;++j)
    safety::separateNodes(*source.boundaries[i].tree,0,*source.boundaries[j].tree,0,bounds[i]+bounds[j]+4*margin,margin,budget);
  for(size_t i=0;i<count;++i)for(size_t j=0;j<count;++j)if(i!=j&&
      safety::inside(candidate.data.v[size_t(candidate.boundaries[i].vertices[0])],candidate,j,budget)!=nesting[i][j])
    throw std::domain_error("Smoothing would change material-boundary nesting.");
  if(!std::isfinite(output.beforeVolume)||!std::isfinite(output.afterVolume)||output.beforeVolume<=0||output.afterVolume<=0)
    throw std::domain_error("Smoothing produced no positive material volume.");
  // A conservative no-op test: absent any source/candidate vertex witnessing a
  // surface change above the margin, refuse rather than sell tangential planar
  // retriangulation as smoothing. This test is not the displacement certificate.
  bool change=false;
  for(size_t i=0;i<vertices.size()&&!change;++i)
    change=source.tree->nearest(candidate.data.v[i]).first>margin||candidate.tree->nearest(vertices[i]).first>margin;
  if(!change)throw std::domain_error("These settings move only the mesh tessellation; no measurable surface change was found.");
  const manifold::Manifold validation(mesh);
  if(validation.Status()!=manifold::Manifold::Error::NoError||validation.NumTri()!=faces.size())
    throw std::domain_error("The smoothed solid could not be validated without changing its triangles.");
  output.boundaries=static_cast<int>(count);return output;
}
} // namespace smooth_safety
