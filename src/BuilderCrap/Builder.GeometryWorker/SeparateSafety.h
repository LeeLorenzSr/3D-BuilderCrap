// Original material-piece partition. The result is source triangle indices,
// never a normalized/repaired replacement mesh.
#pragma once
#include "SimplifySafety.h"
namespace separate_safety {
namespace safety=simplify_safety;
struct Piece {std::vector<int> triangles;int boundaries=1,cavities=0;double volume=0;};
inline std::vector<Piece> separate(const manifold::MeshGL64& raw)try{
  safety::Budget budget;safety::Surface source(raw);const auto margin=source.margin*8;
  safety::collisionNodes(*source.tree,0,0,margin,budget);
  const auto count=source.boundaries.size();if(count>1024)throw std::domain_error("Too many boundaries for bounded material-piece classification.");
  // A vertex classifies a whole connected boundary only after embedded,
  // non-touching surfaces have been checked above; bounds alone are insufficient.
  std::vector<std::vector<bool>> inside(count,std::vector<bool>(count,false));
  std::vector<int> depth(count,0),parent(count,-1);
  for(size_t i=0;i<count;++i)for(size_t j=0;j<count;++j)if(i!=j){inside[i][j]=safety::inside(source.data.v[size_t(source.boundaries[i].vertices[0])],source,j,budget);if(inside[i][j])++depth[i];}
  for(size_t i=0;i<count;++i){
    if((source.boundaries[i].volume>0)!=(depth[i]%2==0))throw std::domain_error("Source shell orientation does not match its nesting. This model cannot be separated safely.");
    for(size_t j=0;j<count;++j)if(inside[i][j]){
      if(inside[j][i])throw std::domain_error("Ambiguous material-boundary containment.");
      for(size_t k=0;k<count;++k)if(inside[i][k]&&j!=k&&!inside[j][k]&&!inside[k][j])throw std::domain_error("Material-boundary containment is not a nested hierarchy.");
      if(depth[j]==depth[i]-1){if(parent[i]>=0)throw std::domain_error("A material boundary has ambiguous ownership.");parent[i]=static_cast<int>(j);}}
    if(depth[i]>0&&parent[i]<0)throw std::domain_error("A material boundary has no unique enclosing owner.");
  }
  std::vector<Piece> result;std::vector<bool> covered(source.data.t.size(),false);
  for(size_t i=0;i<count;++i)if(depth[i]%2==0){
    Piece p;p.triangles=source.boundaries[i].triangles;p.volume=source.boundaries[i].volume;
    for(size_t j=0;j<count;++j)if(parent[j]==static_cast<int>(i)){if(depth[j]%2!=1)throw std::domain_error("Invalid cavity ownership.");++p.boundaries;++p.cavities;p.volume+=source.boundaries[j].volume;p.triangles.insert(p.triangles.end(),source.boundaries[j].triangles.begin(),source.boundaries[j].triangles.end());}
    if(!std::isfinite(p.volume)||p.volume<=0)throw std::domain_error("A separated piece has no positive material volume.");
    std::sort(p.triangles.begin(),p.triangles.end());for(auto face:p.triangles){if(covered[size_t(face)])throw std::domain_error("Duplicate triangle ownership.");covered[size_t(face)]=true;}result.push_back(std::move(p));
  }
  if(std::find(covered.begin(),covered.end(),false)!=covered.end())throw std::domain_error("The material partition omitted triangles.");
  if(result.size()<2)throw std::domain_error("This object already contains one material piece. Its cavities stay with that piece.");
  if(result.size()>1000)throw std::domain_error("Separation exceeds the 1,000-piece limit.");
  std::sort(result.begin(),result.end(),[](const Piece& a,const Piece& b){return a.triangles.front()<b.triangles.front();});return result;
}catch(const std::domain_error& error){
  std::string message=error.what();
  for(const auto& pair:std::array<std::pair<std::string,std::string>,2>{{{"Simplification","Separation"},{"simplification","separation"}}}){
    size_t position=0;while((position=message.find(pair.first,position))!=std::string::npos){message.replace(position,pair.first.size(),pair.second);position+=pair.second.size();}}
  throw std::domain_error(message);
}
} // namespace separate_safety
