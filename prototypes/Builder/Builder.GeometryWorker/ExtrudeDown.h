// Original vertical material closure built through pinned Manifold primitives.
#pragma once
#include "SimplifySafety.h"
namespace extrude_down {
using V=manifold::vec3;
struct Result {manifold::Manifold solid;double before=0,after=0,added=0;};
inline Result calculate(const manifold::Manifold& original,const manifold::MeshGL64& raw,double plane)try{
  if(!std::isfinite(plane)||std::abs(plane)>10000000)throw std::domain_error("Enter a finite world Z plane height within +/-10,000,000 mm.");
  simplify_safety::Budget budget;simplify_safety::Surface source(raw);const auto margin=source.margin*8;
  simplify_safety::collisionNodes(*source.tree,0,0,margin,budget);
  const auto boundaryCount=source.boundaries.size();if(boundaryCount>1024)throw std::domain_error("Too many material boundaries for Extrude down.");
  for(size_t i=0;i<boundaryCount;++i){int depth=0;for(size_t j=0;j<boundaryCount;++j)if(i!=j&&simplify_safety::inside(source.data.v[size_t(source.boundaries[i].vertices[0])],source,j,budget))++depth;
    if((source.boundaries[i].volume>0)!=(depth%2==0))throw std::domain_error("Source shell orientation does not match its material nesting.");}
  std::vector<manifold::Manifold> prisms;
  for(const auto& t:source.data.t){
    const std::array<V,3> top{{source.data.v[size_t(t[0])],source.data.v[size_t(t[1])],source.data.v[size_t(t[2])]}};
    if(linalg::cross(top[1]-top[0],top[2]-top[0]).z<=0)continue;
    std::vector<V> clipped;
    for(size_t i=0;i<3;++i){const auto a=top[i],b=top[(i+1)%3];const bool ai=a.z>=plane,bi=b.z>=plane;
      if(ai)clipped.push_back(a);
      if(ai!=bi){const auto fraction=(plane-a.z)/(b.z-a.z);auto p=a+(b-a)*fraction;p.z=plane;clipped.push_back(p);}}
    if(clipped.size()<3)continue;
    double expected=0;
    for(size_t i=1;i+1<clipped.size();++i){const auto area=linalg::cross(clipped[i]-clipped[0],clipped[i+1]-clipped[0]).z/2;
      expected+=area*((clipped[0].z+clipped[i].z+clipped[i+1].z)/3-plane);}
    if(expected==0)continue;
    if(!std::isfinite(expected)||expected<0)throw std::domain_error("Could not form an upward material face for Extrude down.");
    auto hullPoints=clipped;for(auto p:clipped){p.z=plane;hullPoints.push_back(p);}
    auto prism=manifold::Manifold::Hull(hullPoints);
    if(prism.Status()!=manifold::Manifold::Error::NoError||prism.IsEmpty()||prism.Volume()<=0||std::abs(prism.Volume()-expected)>expected*1e-6)
      throw std::domain_error("A material face could not be extended to the plane safely.");
    prisms.push_back(std::move(prism));
  }
  if(prisms.empty())throw std::domain_error("Nothing extends down to this plane. Choose a lower plane height.");
  const auto extension=manifold::Manifold::BatchBoolean(prisms,manifold::OpType::Add);
  if(extension.Status()!=manifold::Manifold::Error::NoError)throw std::domain_error("Could not join the downward material extensions.");
  const auto added=extension.Boolean(original,manifold::OpType::Subtract);
  if(added.Status()!=manifold::Manifold::Error::NoError)throw std::domain_error("Could not calculate added material.");
  if(added.IsEmpty()||added.Volume()<=0)throw std::domain_error("This plane adds no material. Choose a lower plane or an object with an undercut.");
  const auto result=original.Boolean(extension,manifold::OpType::Add);
  if(result.Status()!=manifold::Manifold::Error::NoError||result.IsEmpty()||result.Volume()<=0)throw std::domain_error("Extrude down could not produce a closed solid.");
  if(result.NumTri()>100000||result.NumPropVert()>300000)throw std::domain_error("The extrusion exceeds the scene geometry limits.");
  return {result,original.Volume(),result.Volume(),added.Volume()};
}catch(const std::domain_error& error){
  std::string message=error.what();for(const auto& word:std::array<std::string,2>{{"Simplification","simplification"}}){size_t p=0;while((p=message.find(word,p))!=std::string::npos){message.replace(p,word.size(),"Extrude down");p+=12;}}
  throw std::domain_error(message);
}
} // namespace extrude_down
