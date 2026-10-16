/* AutoModSync native Linux apply helper.  This small, local-only executable is
 * launched by the client after verified staging has been written.  It accepts
 * no server input: paths come solely from the already-durable pending file. */
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <signal.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

static void die(const char *fmt, ...) { va_list a; va_start(a,fmt); vfprintf(stderr,fmt,a); va_end(a); fputc('\n',stderr); exit(1); }
static int mkdirs(const char *p) { char b[PATH_MAX],*s; if(strlen(p)>=sizeof b)return -1; strcpy(b,p); for(s=b+1;*s;s++)if(*s=='/'){*s=0;if(mkdir(b,0700)&&errno!=EEXIST)return -1;*s='/';} return mkdir(b,0700)&&errno!=EEXIST?-1:0; }
static int safe_rel(const char *p) { return p[0] && p[0]!='/' && !strstr(p,"//") && !strstr(p,"..") && !strchr(p,'\\'); }
static int b64(const char *in,char *out,size_t n) { static const char *t="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"; int v=0,b=-8; size_t o=0; for(;*in && *in!='\r' && *in!='\n';in++){ const char *q=strchr(t,*in); if(*in=='=')break; if(!q)return -1; v=(v<<6)|(int)(q-t); b+=6; if(b>=0){if(o+1>=n)return -1;out[o++]=(char)((v>>b)&255);b-=8;}} out[o]=0; return 0; }
static int cp(const char *src,const char *dst) { int a=open(src,O_RDONLY),b; char buf[65536]; ssize_t r; if(a<0)return -1; b=open(dst,O_WRONLY|O_CREAT|O_TRUNC,0600); if(b<0){close(a);return -1;} while((r=read(a,buf,sizeof buf))>0){char *p=buf; ssize_t l=r;while(l){ssize_t w=write(b,p,l);if(w<=0){close(a);close(b);return -1;}p+=w;l-=w;}} if(r<0||fsync(b)){close(a);close(b);return -1;} close(a);return close(b); }
static void logline(const char *root,const char *fmt,...) { char p[PATH_MAX]; FILE *f; va_list a; snprintf(p,sizeof p,"%s/apply.log",root); f=fopen(p,"a"); if(!f)return; va_start(a,fmt); vfprintf(f,fmt,a); va_end(a); fputc('\n',f); fclose(f); }
static const char *kindroot(char k) { return k=='P'?"plugins":k=='R'?"patchers":k=='C'?"config":NULL; }
static int commit_ownership(const char *root) { char src[PATH_MAX],dir[PATH_MAX],dst[PATH_MAX],tmp[PATH_MAX],last[PATH_MAX],fingerprint[80]; FILE *f; size_t n;
 snprintf(src,sizeof src,"%s/ownership-next.txt",root); f=fopen(src,"r"); if(!f)return 0; if(!fgets(fingerprint,sizeof fingerprint,f)||!fgets(fingerprint,sizeof fingerprint,f)){fclose(f);return -1;} fclose(f); n=strcspn(fingerprint,"\r\n"); fingerprint[n]=0; if(n!=64)return -1; for(size_t i=0;i<n;i++)if(!strchr("0123456789abcdefABCDEF",fingerprint[i]))return -1;
 snprintf(dir,sizeof dir,"%s/ownership",root); if(mkdirs(dir))return -1; snprintf(dst,sizeof dst,"%s/%s.txt",dir,fingerprint); snprintf(tmp,sizeof tmp,"%s.tmp",dst); if(cp(src,tmp)||rename(tmp,dst)){unlink(tmp);return -1;} snprintf(last,sizeof last,"%s/last-successful-server.txt",root); f=fopen(last,"w"); if(!f)return -1; if(fprintf(f,"%s\n",fingerprint)<0||fflush(f)||fsync(fileno(f))){fclose(f);return -1;} fclose(f); return unlink(src); }
static int apply(const char *root) {
 char pending[PATH_MAX],line[PATH_MAX*2],game[PATH_MAX],bep[PATH_MAX]; FILE *f; int versioned=0;
 snprintf(pending,sizeof pending,"%s/pending.txt",root); f=fopen(pending,"r"); if(!f)return -1;
 { char parent[PATH_MAX]; snprintf(parent,sizeof parent,"%s/..",root); if(!realpath(parent,bep)){fclose(f);return -1;} snprintf(parent,sizeof parent,"%s/..",bep); if(!realpath(parent,game)){fclose(f);return -1;} }
 while(fgets(line,sizeof line,f)) { size_t z=strcspn(line,"\r\n"); line[z]=0; if(!line[0])continue; if(!strcmp(line,"AMSPENDING2")){versioned=1;continue;} char op='W',k=0,rel[PATH_MAX]={0};
   if(versioned){ char *a=strtok(line,"|"),*b=strtok(NULL,"|"),*c=strtok(NULL,"|"); if(!a||!b||!c||strlen(a)!=1||strlen(b)!=1){fclose(f);return -1;} op=a[0];k=b[0]; if(op=='W'){if(b64(c,rel,sizeof rel)){fclose(f);return -1;}} else { char *digest=strtok(NULL,"|"),*path=strtok(NULL,"|"); if(!digest||!path||b64(path,rel,sizeof rel)){fclose(f);return -1;} } }
   else { if(strlen(line)<3||line[1]!=':'){fclose(f);return -1;} k=line[0]; strncpy(rel,line+2,sizeof rel-1); }
   const char *kr=kindroot(k); if(!kr||!safe_rel(rel)||(op!='W'&&op!='D')){fclose(f);return -1;} char dstroot[PATH_MAX],dst[PATH_MAX],src[PATH_MAX],parent[PATH_MAX],tmp[PATH_MAX];
   snprintf(dstroot,sizeof dstroot,"%s/%s",bep,kr); snprintf(dst,sizeof dst,"%s/%s",dstroot,rel); if(op=='D'){ if(unlink(dst)&&errno!=ENOENT){fclose(f);return -1;} logline(root,"DELETE %c:%s",k,rel); continue; }
   snprintf(src,sizeof src,"%s/staging/%s/%s.amsnew",root,kr,rel); struct stat st; if(lstat(src,&st)||!S_ISREG(st.st_mode)){fclose(f);return -1;} strncpy(parent,dst,sizeof parent-1); char *slash=strrchr(parent,'/'); if(!slash){fclose(f);return -1;} *slash=0; if(mkdirs(parent)){fclose(f);return -1;} snprintf(tmp,sizeof tmp,"%s.amstxnnew",dst); if(cp(src,tmp)||rename(tmp,dst)){unlink(tmp);fclose(f);return -1;} if(unlink(src)){fclose(f);return -1;} logline(root,"WRITE %c:%s",k,rel);
 }
 fclose(f); if(commit_ownership(root)||unlink(pending))return -1; logline(root,"Apply complete; launching Steam."); return 0;
}
int main(int argc,char **argv) { if(argc!=4||strcmp(argv[1],"--apply-pending"))die("usage: %s --apply-pending PID AMSROOT",argv[0]); char *e; long p=strtol(argv[2],&e,10); if(!*argv[3]||*e||p<=0)die("invalid arguments"); while(kill((pid_t)p,0)==0||errno==EPERM){ struct timespec pause={0,250000000L}; nanosleep(&pause,0); } if(apply(argv[3])){logline(argv[3],"Apply failed: %s",strerror(errno));return 1;} execlp("steam","steam","-applaunch","892970",(char*)0); logline(argv[3],"Apply complete but Steam launch failed: %s",strerror(errno));return 1; }
