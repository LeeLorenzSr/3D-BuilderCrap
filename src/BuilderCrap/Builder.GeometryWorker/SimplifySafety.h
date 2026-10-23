// Original bounded simplification verification. No UI or document state lives here.
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <map>
#include <memory>
#include <numeric>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace simplify_safety {
using V=manifold::vec3;
using T=std::array<int,3>;
inline double sq(V p){return linalg::dot(p,p);}
inline std::array<double,3> key(V p){return {p.x,p.y,p.z};}
struct Budget {
  size_t patches=2000000,pairs=4000000,classifications=2000000;
  static void spend(size_t& remaining,const char* kind){if(remaining==0)throw std::domain_error(std::string("Simplification exceeded its bounded ")+kind+" verification work. Try less reduction or a different deviation limit.");--remaining;}
};
struct Box {
  V lo=V(INFINITY),hi=V(-INFINITY);
  void add(V p){lo=linalg::min(lo,p);hi=linalg::max(hi,p);}
  void add(const Box& b){add(b.lo);add(b.hi);}
};
inline double boxDistance(const Box& a,const Box& b){V gap=linalg::max(linalg::max(a.lo-b.hi,b.lo-a.hi),V(0.0));return sq(gap);}
inline double pointBox(V p,const Box& b){return sq(linalg::max(linalg::max(b.lo-p,p-b.hi),V(0.0)));}
inline double segmentDistance(V p,V a,V b){const auto d=b-a;const auto n=sq(d);return sq(p-(a+d*(n==0?0:std::clamp(linalg::dot(p-a,d)/n,0.0,1.0))));}
// Closest point on a nondegenerate triangle, using Voronoi-region barycentrics.
inline double pointTriangle(V p,V a,V b,V c){
  const auto ab=b-a,ac=c-a,ap=p-a;const double d1=linalg::dot(ab,ap),d2=linalg::dot(ac,ap);
  if(d1<=0&&d2<=0)return sq(ap);
  const auto bp=p-b;const double d3=linalg::dot(ab,bp),d4=linalg::dot(ac,bp);
  if(d3>=0&&d4<=d3)return sq(bp);
  const double vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return sq(p-(a+ab*std::clamp(d1/(d1-d3),0.0,1.0)));
  const auto cp=p-c;const double d5=linalg::dot(ab,cp),d6=linalg::dot(ac,cp);
  if(d6>=0&&d5<=d6)return sq(cp);
  const double vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return sq(p-(a+ac*std::clamp(d2/(d2-d6),0.0,1.0)));
  const double va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return sq(p-(b+(c-b)*std::clamp((d4-d3)/((d4-d3)+(d5-d6)),0.0,1.0)));
  const auto denominator=va+vb+vc;
  if(denominator<=0||!std::isfinite(denominator))return std::min({segmentDistance(p,a,b),segmentDistance(p,b,c),segmentDistance(p,c,a)});
  // Even under ill conditioning, the certificate must use a point on this
  // convex target triangle rather than an unconstrained extrapolation.
  double u=std::clamp(va/denominator,0.0,1.0),v=std::clamp(vb/denominator,0.0,1.0),w=std::clamp(vc/denominator,0.0,1.0);
  const auto sum=u+v+w;if(!std::isfinite(sum)||sum==0)return sq(p-a);u/=sum;v/=sum;w/=sum;
  return sq(p-(a*u+b*v+c*w));
}
inline double segmentSegment(V p,V q,V a,V b){
  const auto u=q-p,v=b-a,w=p-a;const double aa=sq(u),bb=linalg::dot(u,v),cc=sq(v),dd=linalg::dot(u,w),ee=linalg::dot(v,w);
  double s=0,t=0;const auto den=aa*cc-bb*bb;
  if(aa==0)return segmentDistance(p,a,b);if(cc==0)return segmentDistance(a,p,q);
  if(den>0)s=std::clamp((bb*ee-cc*dd)/den,0.0,1.0);
  t=(bb*s+ee)/cc;if(t<0){t=0;s=std::clamp(-dd/aa,0.0,1.0);}else if(t>1){t=1;s=std::clamp((bb-dd)/aa,0.0,1.0);}
  return sq(p+u*s-a-v*t);
}
struct Data {std::vector<V> v;std::vector<T> t;};
inline std::array<V,3> triangle(const Data& d,int i){const auto& t=d.t[size_t(i)];return {d.v[size_t(t[0])],d.v[size_t(t[1])],d.v[size_t(t[2])]};}
struct Tree {
  struct Node {Box box;int begin=0,end=0,left=-1,right=-1;bool leaf()const{return left<0;}};
  const Data* data;std::vector<int> order;std::vector<Node> nodes;
  Tree(const Data& d,std::vector<int> ids):data(&d),order(std::move(ids)){nodes.reserve(2*order.size());build(0,static_cast<int>(order.size()));}
  int build(int begin,int end){
    Node n;n.begin=begin;n.end=end;Box centers;
    for(int i=begin;i<end;++i){const auto tri=triangle(*data,order[size_t(i)]);for(auto p:tri)n.box.add(p);centers.add((tri[0]+tri[1]+tri[2])/3.0);}
    const auto id=static_cast<int>(nodes.size());nodes.push_back(n);
    if(end-begin>8){const auto span=centers.hi-centers.lo;const int axis=span.x>=span.y&&span.x>=span.z?0:span.y>=span.z?1:2;const auto mid=(begin+end)/2;
      std::nth_element(order.begin()+begin,order.begin()+mid,order.begin()+end,[&](int a,int b){const auto x=triangle(*data,a),y=triangle(*data,b);return x[0][axis]+x[1][axis]+x[2][axis]<y[0][axis]+y[1][axis]+y[2][axis];});
      const auto left=build(begin,mid),right=build(mid,end);nodes[size_t(id)].left=left;nodes[size_t(id)].right=right;}
    return id;
  }
  std::pair<double,int> nearest(V p)const{
    double best=INFINITY;int face=-1;std::vector<int> stack{0};
    while(!stack.empty()){const int id=stack.back();stack.pop_back();const auto& n=nodes[size_t(id)];if(pointBox(p,n.box)>best)continue;
      if(n.leaf()){for(int i=n.begin;i<n.end;++i){const auto index=order[size_t(i)];const auto tri=triangle(*data,index);const double d=pointTriangle(p,tri[0],tri[1],tri[2]);if(d<best){best=d;face=index;}}}
      else {const auto l=pointBox(p,nodes[size_t(n.left)].box),r=pointBox(p,nodes[size_t(n.right)].box);if(l<r){stack.push_back(n.right);stack.push_back(n.left);}else{stack.push_back(n.left);stack.push_back(n.right);}}}
    if(face<0||!std::isfinite(best))throw std::domain_error("Could not measure simplification surface distance.");return {std::sqrt(std::max(0.0,best)),face};
  }
};
struct Boundary {std::vector<int> triangles,vertices;int euler=0;double volume=0;std::unique_ptr<Tree> tree;};
struct Surface {
  Data data;std::vector<Boundary> boundaries;std::unique_ptr<Tree> tree;double margin=0;
  explicit Surface(const manifold::MeshGL64& mesh){
    std::vector<uint64_t> alias(mesh.NumVert());std::iota(alias.begin(),alias.end(),uint64_t(0));
    for(size_t i=0;i<mesh.mergeFromVert.size();++i)alias[size_t(mesh.mergeFromVert[i])]=mesh.mergeToVert[i];
    std::map<std::array<double,3>,int> unique;
    for(size_t f=0;f<mesh.triVerts.size();f+=3){T tri{};
      for(int k=0;k<3;++k){auto index=mesh.triVerts[f+size_t(k)];size_t hops=0;while(alias[size_t(index)]!=index){index=alias[size_t(index)];if(++hops>alias.size())throw std::domain_error("Invalid mesh aliases.");}
        V p(mesh.vertProperties[size_t(index*mesh.numProp)],mesh.vertProperties[size_t(index*mesh.numProp+1)],mesh.vertProperties[size_t(index*mesh.numProp+2)]);
        if(!std::isfinite(p.x)||!std::isfinite(p.y)||!std::isfinite(p.z))throw std::domain_error("Nonfinite simplification geometry.");
        const auto [it,added]=unique.emplace(key(p),static_cast<int>(data.v.size()));if(added)data.v.push_back(p);tri[size_t(k)]=it->second;}
      const auto a=data.v[size_t(tri[0])],b=data.v[size_t(tri[1])],c=data.v[size_t(tri[2])];
      if(sq(linalg::cross(b-a,c-a))==0)throw std::domain_error("Simplification needs nondegenerate closed triangles.");data.t.push_back(tri);}
    if(data.t.empty())throw std::domain_error("Simplification produced no surface.");
    std::map<std::pair<int,int>,int> edges;std::vector<std::vector<int>> adjacency(data.t.size());
    for(size_t f=0;f<data.t.size();++f)for(int i=0;i<3;++i){const auto& t=data.t[f];if(!edges.emplace(std::make_pair(t[size_t(i)],t[size_t((i+1)%3)]),static_cast<int>(f)).second)throw std::domain_error("Duplicate or nonmanifold surface edge.");}
    for(const auto& [edge,face]:edges){const auto reverse=edges.find({edge.second,edge.first});if(reverse==edges.end())throw std::domain_error("Simplification needs consistently closed boundaries.");adjacency[size_t(face)].push_back(reverse->second);}
    std::vector<std::vector<int>> incident(data.v.size());for(size_t f=0;f<data.t.size();++f)for(auto v:data.t[f])incident[size_t(v)].push_back(static_cast<int>(f));
    std::vector<int> fanStamp(data.t.size(),-1);
    for(size_t v=0;v<incident.size();++v){std::vector<int> stack{incident[v][0]};size_t fan=0;while(!stack.empty()){const auto f=stack.back();stack.pop_back();if(fanStamp[size_t(f)]==static_cast<int>(v))continue;fanStamp[size_t(f)]=static_cast<int>(v);++fan;
        for(auto neighbor:adjacency[size_t(f)])if(std::find(data.t[size_t(neighbor)].begin(),data.t[size_t(neighbor)].end(),static_cast<int>(v))!=data.t[size_t(neighbor)].end())stack.push_back(neighbor);}
      if(fan!=incident[v].size())throw std::domain_error("Simplification needs one manifold triangle fan at every vertex.");}
    std::vector<bool> visited(data.t.size(),false);std::vector<int> all(data.t.size());std::iota(all.begin(),all.end(),0);tree=std::make_unique<Tree>(data,all);
    const auto& box=tree->nodes[0].box;const auto mag=std::max({1.0,std::abs(box.lo.x),std::abs(box.lo.y),std::abs(box.lo.z),std::abs(box.hi.x),std::abs(box.hi.y),std::abs(box.hi.z),linalg::length(box.hi-box.lo)});margin=mag*1e-12;
    for(size_t first=0;first<data.t.size();++first)if(!visited[first]){
      Boundary b;std::vector<int> stack{static_cast<int>(first)};std::set<int> verts;
      while(!stack.empty()){const auto f=stack.back();stack.pop_back();if(visited[size_t(f)])continue;visited[size_t(f)]=true;b.triangles.push_back(f);for(auto v:data.t[size_t(f)])verts.insert(v);for(auto n:adjacency[size_t(f)])stack.push_back(n);}
      b.vertices.assign(verts.begin(),verts.end());b.euler=static_cast<int>(verts.size())-static_cast<int>(b.triangles.size())/2;
      if(b.euler>2||b.euler%2!=0)throw std::domain_error("Unsupported boundary topology.");
      const auto origin=data.v[size_t(b.vertices[0])];for(auto f:b.triangles){const auto t=triangle(data,f);b.volume+=linalg::dot(t[0]-origin,linalg::cross(t[1]-origin,t[2]-origin))/6;}
      if(!std::isfinite(b.volume)||std::abs(b.volume)<=margin*margin*margin)throw std::domain_error("Simplification collapsed a material boundary.");
      b.tree=std::make_unique<Tree>(data,b.triangles);boundaries.push_back(std::move(b));}
  }
  Surface(const Surface&)=delete;Surface& operator=(const Surface&)=delete;
};
inline bool inTriangle(V p,const std::array<V,3>& t,double epsilon){
  const auto n=linalg::cross(t[1]-t[0],t[2]-t[0]);const auto nn=linalg::length(n);
  if(std::abs(linalg::dot(p-t[0],n))>epsilon*nn)return false;
  const double tolerance=epsilon*nn*std::max({linalg::length(t[1]-t[0]),linalg::length(t[2]-t[1]),linalg::length(t[0]-t[2])});
  for(int i=0;i<3;++i)if(linalg::dot(linalg::cross(t[size_t((i+1)%3)]-t[size_t(i)],p-t[size_t(i)]),n)<-tolerance)return false;
  return true;
}
inline double cross2(V a,V b,int drop){const int x=(drop+1)%3,y=(drop+2)%3;return a[x]*b[y]-a[y]*b[x];}
// Intersection is allowed only on the topologically shared edge/vertex. Pairs
// sharing a vertex are still checked for intersections away from that vertex.
inline bool intersectsBeyondShared(const Data& d,int ai,int bi,double epsilon){
  const auto a=triangle(d,ai),b=triangle(d,bi);std::vector<V> shared;
  for(auto av:d.t[size_t(ai)])for(auto bv:d.t[size_t(bi)])if(av==bv)shared.push_back(d.v[size_t(av)]);
  auto allowed=[&](V p){if(shared.size()==1)return sq(p-shared[0])<=epsilon*epsilon;if(shared.size()==2)return segmentDistance(p,shared[0],shared[1])<=epsilon*epsilon;return false;};
  if(shared.size()>2)return true;
  for(auto p:a)if(inTriangle(p,b,epsilon)&&!allowed(p))return true;
  for(auto p:b)if(inTriangle(p,a,epsilon)&&!allowed(p))return true;
  auto edgeHits=[&](const std::array<V,3>& first,const std::array<V,3>& second){
    const auto normal=linalg::cross(second[1]-second[0],second[2]-second[0]);const auto length=linalg::length(normal);
    for(int i=0;i<3;++i){const auto p=first[size_t(i)],q=first[size_t((i+1)%3)],dir=q-p;const auto denominator=linalg::dot(dir,normal);
      if(std::abs(denominator)>epsilon*length){const auto t=linalg::dot(second[0]-p,normal)/denominator;if(t>=0&&t<=1){const auto hit=p+dir*t;if(inTriangle(hit,second,epsilon)&&!allowed(hit))return true;}}
      else if(std::abs(linalg::dot(p-second[0],normal))<=epsilon*length&&std::abs(linalg::dot(q-second[0],normal))<=epsilon*length){
        const auto an=linalg::abs(normal);const int drop=an.x>=an.y&&an.x>=an.z?0:an.y>=an.z?1:2;
        for(int j=0;j<3;++j){const auto r=second[size_t(j)],s=second[size_t((j+1)%3)],v=s-r;const auto den=cross2(dir,v,drop);
          // Nearly parallel projected lines make the intersection quotient
          // ill-conditioned. Treat these as tolerance-collinear segments;
          // their physical separation/overlap is checked by clamped distances.
          if(std::abs(den)>epsilon*(linalg::length(dir)+linalg::length(v))){const auto t=cross2(r-p,v,drop)/den,u=cross2(r-p,dir,drop)/den;if(t>=0&&t<=1&&u>=0&&u<=1){const auto hit=p+dir*t;
            if(sq(hit-(r+v*u))>epsilon*epsilon)throw std::domain_error("A nearly parallel surface contact is too ambiguous to verify safely.");if(!allowed(hit))return true;}}
          else {for(auto hit:{p,q,r,s})if(segmentDistance(hit,p,q)<=epsilon*epsilon&&segmentDistance(hit,r,s)<=epsilon*epsilon&&!allowed(hit))return true;}}
      }}return false;};
  return edgeHits(a,b)||edgeHits(b,a);
}
inline void collisionNodes(const Tree& tree,int ai,int bi,double epsilon,Budget& budget){
  const auto& a=tree.nodes[size_t(ai)];const auto& b=tree.nodes[size_t(bi)];if(boxDistance(a.box,b.box)>epsilon*epsilon)return;
  if(a.leaf()&&b.leaf()){
    for(int i=a.begin;i<a.end;++i)for(int j=b.begin;j<b.end;++j){const auto x=tree.order[size_t(i)],y=tree.order[size_t(j)];if(ai==bi&&x>=y)continue;Box xb,yb;for(auto p:triangle(*tree.data,x))xb.add(p);for(auto p:triangle(*tree.data,y))yb.add(p);if(boxDistance(xb,yb)>epsilon*epsilon)continue;Budget::spend(budget.pairs,"contact");
      if(intersectsBeyondShared(*tree.data,x,y,epsilon))throw std::domain_error("Simplification would intersect or touch material boundaries (faces "+std::to_string(x)+", "+std::to_string(y)+").");}return;}
  if(ai==bi){collisionNodes(tree,a.left,a.left,epsilon,budget);collisionNodes(tree,a.left,a.right,epsilon,budget);collisionNodes(tree,a.right,a.right,epsilon,budget);}
  else if(!a.leaf()&&(b.leaf()||a.end-a.begin>=b.end-b.begin)){collisionNodes(tree,a.left,bi,epsilon,budget);collisionNodes(tree,a.right,bi,epsilon,budget);}
  else {collisionNodes(tree,ai,b.left,epsilon,budget);collisionNodes(tree,ai,b.right,epsilon,budget);}
}
inline bool inside(V p,const Surface& s,size_t boundary,Budget& budget){
  const auto& b=s.boundaries[boundary];const auto& box=b.tree->nodes[0].box;
  if(p.x<box.lo.x||p.y<box.lo.y||p.z<box.lo.z||p.x>box.hi.x||p.y>box.hi.y||p.z>box.hi.z)return false;
  double angle=0;
  for(auto face:b.triangles){Budget::spend(budget.classifications,"nesting");const auto t=triangle(s.data,face);const auto a=t[0]-p,c=t[2]-p,d=t[1]-p;const auto al=linalg::length(a),dl=linalg::length(d),cl=linalg::length(c);
    angle+=2*std::atan2(linalg::dot(a,linalg::cross(d,c)),al*dl*cl+linalg::dot(a,d)*cl+linalg::dot(d,c)*al+linalg::dot(c,a)*dl);}
  if(!std::isfinite(angle))throw std::domain_error("Could not classify boundary nesting.");return std::abs(angle)>6.283185307179586;
}
inline std::vector<int> correspondence(const Surface& source,const Surface& candidate){
  if(source.boundaries.size()!=candidate.boundaries.size())throw std::domain_error("Simplification would remove or split a material boundary.");
  std::map<std::array<double,3>,int> owner;
  for(size_t i=0;i<source.boundaries.size();++i)for(auto v:source.boundaries[i].vertices){const auto [it,added]=owner.emplace(key(source.data.v[size_t(v)]),static_cast<int>(i));if(!added&&it->second!=static_cast<int>(i))throw std::domain_error("Source boundaries touch ambiguously.");}
  std::vector<int> mapping(source.boundaries.size(),-1);
  for(size_t j=0;j<candidate.boundaries.size();++j){int original=-1;
    for(auto v:candidate.boundaries[j].vertices){const auto found=owner.find(key(candidate.data.v[size_t(v)]));if(found==owner.end())throw std::domain_error("Simplification introduced an untracked vertex.");if(original<0)original=found->second;else if(original!=found->second)throw std::domain_error("Simplification merged material boundaries.");}
    if(original<0||mapping[size_t(original)]>=0)throw std::domain_error("Simplification split material boundaries.");mapping[size_t(original)]=static_cast<int>(j);
    const auto& a=source.boundaries[size_t(original)];const auto& b=candidate.boundaries[j];
    if(a.euler!=b.euler||(a.volume>0)!=(b.volume>0))throw std::domain_error("Simplification would change a boundary genus or orientation.");}
  return mapping;
}
struct Patch {V a,b,c;int depth;};
inline double certify(const Surface& source,size_t sb,const Surface& target,size_t tb,double limit,double margin,Budget& budget){
  double bound=0;const auto& tree=*target.boundaries[tb].tree;
  for(auto face:source.boundaries[sb].triangles){const auto t=triangle(source.data,face);std::vector<Patch> stack{{t[0],t[1],t[2],0}};
    while(!stack.empty()){Budget::spend(budget.patches,"surface-bound");const auto p=stack.back();stack.pop_back();const auto center=(p.a+p.b+p.c)/3.0;const auto nearest=tree.nearest(center);
      if(nearest.first>limit+margin)throw std::domain_error("Simplification exceeds the requested surface-deviation limit.");
      const auto targetTri=triangle(target.data,nearest.second);
      const auto upper=std::sqrt(std::max({pointTriangle(p.a,targetTri[0],targetTri[1],targetTri[2]),pointTriangle(p.b,targetTri[0],targetTri[1],targetTri[2]),pointTriangle(p.c,targetTri[0],targetTri[1],targetTri[2])}))+margin;
      // Distance to this fixed convex triangle is convex. Its maximum at the
      // patch vertices therefore bounds every point on this entire patch.
      if(upper<=limit){bound=std::max(bound,upper);continue;}
      if(p.depth>=12)throw std::domain_error("Simplification surface bound could not be established at the bounded refinement depth.");
      const auto ab=(p.a+p.b)/2.0,bc=(p.b+p.c)/2.0,ca=(p.c+p.a)/2.0;const int depth=p.depth+1;
      stack.push_back({p.a,ab,ca,depth});stack.push_back({ab,p.b,bc,depth});stack.push_back({ca,bc,p.c,depth});stack.push_back({ab,bc,ca,depth});
    }}return bound;
}
inline double triangleDistance(const std::array<V,3>& a,const std::array<V,3>& b){
  double best=INFINITY;for(auto p:a)best=std::min(best,pointTriangle(p,b[0],b[1],b[2]));for(auto p:b)best=std::min(best,pointTriangle(p,a[0],a[1],a[2]));
  for(int i=0;i<3;++i)for(int j=0;j<3;++j)best=std::min(best,segmentSegment(a[size_t(i)],a[size_t((i+1)%3)],b[size_t(j)],b[size_t((j+1)%3)]));return best;
}
inline void gapNodes(const Tree& a,int ai,const Tree& b,int bi,double& best,Budget& budget){
  const auto& x=a.nodes[size_t(ai)];const auto& y=b.nodes[size_t(bi)];if(boxDistance(x.box,y.box)>=best)return;
  if(x.leaf()&&y.leaf()){for(int i=x.begin;i<x.end;++i)for(int j=y.begin;j<y.end;++j){Budget::spend(budget.pairs,"separation");best=std::min(best,triangleDistance(triangle(*a.data,a.order[size_t(i)]),triangle(*b.data,b.order[size_t(j)])));}return;}
  if(!x.leaf()&&(y.leaf()||x.end-x.begin>=y.end-y.begin)){gapNodes(a,x.left,b,bi,best,budget);gapNodes(a,x.right,b,bi,best,budget);}
  else {gapNodes(a,ai,b,y.left,best,budget);gapNodes(a,ai,b,y.right,best,budget);}
}
inline Box patchBox(const std::array<V,3>& t){Box b;for(auto v:t)b.add(v);return b;}
inline double diameterSquared(const std::array<V,3>& t){return std::max({sq(t[0]-t[1]),sq(t[1]-t[2]),sq(t[2]-t[0])});}
// Separate surfaces are certified by lower AABB distance, not by treating an
// approximate closest-point distance as a rigorous lower bound.
inline void separatePatches(const std::array<V,3>& a,const std::array<V,3>& b,double limit,double margin,int depth,Budget& budget){
  Budget::spend(budget.pairs,"separation");if(boxDistance(patchBox(a),patchBox(b))>std::pow(limit+margin,2))return;
  if(depth>=12)throw std::domain_error("Material-boundary separation could not be verified at bounded refinement depth.");
  if(diameterSquared(a)>=diameterSquared(b)){
    const auto ab=(a[0]+a[1])/2.0,bc=(a[1]+a[2])/2.0,ca=(a[2]+a[0])/2.0;
    for(const auto& t:std::array<std::array<V,3>,4>{{{a[0],ab,ca},{ab,a[1],bc},{ca,bc,a[2]},{ab,bc,ca}}})separatePatches(t,b,limit,margin,depth+1,budget);
  }else separatePatches(b,a,limit,margin,depth,budget);
}
inline void separateNodes(const Tree& a,int ai,const Tree& b,int bi,double limit,double margin,Budget& budget){
  const auto& x=a.nodes[size_t(ai)];const auto& y=b.nodes[size_t(bi)];if(boxDistance(x.box,y.box)>std::pow(limit+margin,2))return;
  if(x.leaf()&&y.leaf()){for(int i=x.begin;i<x.end;++i)for(int j=y.begin;j<y.end;++j)separatePatches(triangle(*a.data,a.order[size_t(i)]),triangle(*b.data,b.order[size_t(j)]),limit,margin,0,budget);return;}
  if(!x.leaf()&&(y.leaf()||x.end-x.begin>=y.end-y.begin)){separateNodes(a,x.left,b,bi,limit,margin,budget);separateNodes(a,x.right,b,bi,limit,margin,budget);}
  else {separateNodes(a,ai,b,y.left,limit,margin,budget);separateNodes(a,ai,b,y.right,limit,margin,budget);}
}
struct Result {manifold::Manifold solid;double bound=0,tolerance=0,beforeVolume=0,afterVolume=0;int boundaries=0,cavities=0;};
inline Result simplify(const manifold::Manifold& original,const manifold::MeshGL64& raw,double reduction,double maximum){
  if(!std::isfinite(reduction)||reduction<=0||reduction>=100||!std::isfinite(maximum)||maximum<=0||maximum>1000)throw std::domain_error("Choose finite reduction between 0% and 100% and a positive deviation at most 1,000 mm.");
  if(original.GetTolerance()>maximum)throw std::domain_error("The requested deviation is below this geometry's existing numerical tolerance.");
  Budget budget;Surface source(raw);const auto margin=source.margin*8;
  if(maximum<=margin)throw std::domain_error("The requested deviation is below the numerical verification margin at these coordinates.");
  collisionNodes(*source.tree,0,0,margin,budget);
  const auto count=source.boundaries.size();if(count>1024)throw std::domain_error("Too many material boundaries for bounded simplification verification.");
  std::vector<std::vector<bool>> nesting(count,std::vector<bool>(count,false));std::vector<std::vector<double>> gaps(count,std::vector<double>(count,INFINITY));
  for(size_t i=0;i<count;++i)for(size_t j=0;j<count;++j)if(i!=j)nesting[i][j]=inside(source.data.v[size_t(source.boundaries[i].vertices[0])],source,j,budget);
  for(size_t i=0;i<count;++i){const auto depth=std::count(nesting[i].begin(),nesting[i].end(),true);
    if((source.boundaries[i].volume>0)!=(depth%2==0))throw std::domain_error("Source shell orientation does not match its nesting. This model cannot be simplified safely.");}
  for(size_t i=0;i<count;++i)for(size_t j=i+1;j<count;++j){double gap=std::pow(2*maximum+16*margin,2);gapNodes(*source.boundaries[i].tree,0,*source.boundaries[j].tree,0,gap,budget);gaps[i][j]=gaps[j][i]=std::sqrt(gap);if(gaps[i][j]<=4*margin)throw std::domain_error("Source boundaries touch or are too close to verify.");}
  struct Candidate {manifold::Manifold solid;double tolerance;int faces;};std::vector<Candidate> candidates;
  const int target=static_cast<int>(std::ceil(source.data.t.size()*(1-reduction/100)));
  for(double fraction:{0.0,0.0625,0.125,0.25,0.5,1.0}){const auto tolerance=std::max(original.GetTolerance(),maximum*fraction);auto s=original.Simplify(tolerance);const auto faces=s.NumTri();if(s.Status()==manifold::Manifold::Error::NoError&&faces>0&&faces<source.data.t.size())candidates.push_back({s,tolerance,static_cast<int>(faces)});}
  std::stable_sort(candidates.begin(),candidates.end(),[&](const Candidate& a,const Candidate& b){const auto ad=std::abs(a.faces-target),bd=std::abs(b.faces-target);return ad!=bd?ad<bd:a.tolerance<b.tolerance;});
  std::string last="No strict triangle reduction was found within the requested shape limit.";
  for(const auto& c:candidates)try{
    Surface result(c.solid.GetMeshGL64());const auto mapping=correspondence(source,result);collisionNodes(*result.tree,0,0,margin,budget);
    std::vector<double> bounds(count,0);
    for(size_t i=0;i<count;++i){bounds[i]=std::max(certify(source,i,result,size_t(mapping[i]),maximum,margin,budget),certify(result,size_t(mapping[i]),source,i,maximum,margin,budget));}
    // Tighten only when needed: a planar thin-wall cleanup can have a much
    // smaller actual certificate than the user's maximum permitted deviation.
    for(size_t i=0;i<count;++i){double limit=maximum;for(size_t j=0;j<count;++j)if(i!=j&&bounds[i]+bounds[j]+4*margin>=gaps[i][j])limit=std::min(limit,gaps[i][j]/4);
      if(limit<maximum)bounds[i]=std::max(certify(source,i,result,size_t(mapping[i]),limit,margin,budget),certify(result,size_t(mapping[i]),source,i,limit,margin,budget));}
    for(size_t i=0;i<count;++i)for(size_t j=i+1;j<count;++j)separateNodes(*source.boundaries[i].tree,0,*source.boundaries[j].tree,0,bounds[i]+bounds[j]+4*margin,margin,budget);
    for(size_t i=0;i<count;++i)for(size_t j=0;j<count;++j)if(i!=j){
      if(inside(result.data.v[size_t(result.boundaries[size_t(mapping[i])].vertices[0])],result,size_t(mapping[j]),budget)!=nesting[i][j])throw std::domain_error("Simplification would change cavity nesting.");}
    double before=0,after=0;int cavities=0;for(const auto& b:source.boundaries){before+=b.volume;if(b.volume<0)++cavities;}for(const auto& b:result.boundaries)after+=b.volume;
    if(before<=0||after<=0)throw std::domain_error("Simplification produced no positive material volume.");
    return {c.solid,*std::max_element(bounds.begin(),bounds.end()),c.tolerance,before,after,static_cast<int>(count),cavities};
  }catch(const std::domain_error& e){last=e.what();}
  throw std::domain_error("No safe reduction found: "+last);
}
} // namespace simplify_safety
