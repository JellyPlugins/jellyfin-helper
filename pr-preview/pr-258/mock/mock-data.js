// Demo library. One coherent set across Overview, Trends, Codecs, Explorer,
// Arr and Recommendations: every count matches its paths, every size matches
// FileSizes, every referenced title exists. Scale mirrors a real instance.
var MOCK_TRANSLATIONS = null;
function _lib(n,t,o){var s=(o.VideoSize||0)+(o.AudioSize||0)+(o.SubtitleSize||0)+(o.ImageSize||0)+(o.NfoSize||0)+(o.TrickplaySize||0)+(o.BookSize||0)+(o.OtherSize||0);return {LibraryName:n,CollectionType:t,RootPaths:o.RootPaths||[],VideoSize:0,AudioSize:0,SubtitleSize:0,ImageSize:0,NfoSize:0,TrickplaySize:0,BookSize:0,OtherSize:0,VideoFileCount:0,AudioFileCount:0,SubtitleFileCount:0,ImageFileCount:0,NfoFileCount:0,TrickplayFileCount:0,TrickplayFolderCount:o.TrickplayFolderCount||0,BookFileCount:0,OtherFileCount:0,TotalSize:s,VideoCodecs:{},VideoAudioCodecs:{},MusicAudioCodecs:{},BookFormats:{},BookFormatSizes:{},ContainerFormats:{},Resolutions:{},VideoCodecPaths:{},VideoAudioCodecPaths:{},MusicAudioCodecPaths:{},BookFormatPaths:{},ContainerFormatPaths:{},ResolutionPaths:{},VideoCodecSizes:{},VideoAudioCodecSizes:{},MusicAudioCodecSizes:{},ContainerSizes:{},ResolutionSizes:{},VideosWithoutSubtitles:0,VideosWithoutImages:0,VideosWithoutNfo:0,OrphanedMetadataDirectories:0,VideosWithoutSubtitlesPaths:[],VideosWithoutImagesPaths:[],VideosWithoutNfoPaths:[],OrphanedMetadataDirectoriesPaths:[],...o};}

// Seeded generator, so the demo is stable across reloads.
var _mseed = 987654321;
function _mrnd(){_mseed=(_mseed*16807)%2147483647;return(_mseed-1)/2147483646;}
function _mri(n){return Math.floor(_mrnd()*n);}
function _mbytes(lo,hi){return Math.floor(lo+_mrnd()*(hi-lo));}
function _mpad(n,l){var s=String(n);while(s.length<(l||2)){s="0"+s;}return s;}
var _musedIds={};
function _mimdb(){var id;do{id="tt"+(1000000+_mri(9000000));}while(_musedIds[id]);_musedIds[id]=true;return id;}
function _brTier(br){return br<2?"< 2 Mbps":(br<4?"2–4 Mbps":(br<8?"4–8 Mbps":(br<16?"8–16 Mbps":(br<32?"16–32 Mbps":(br<60?"32–60 Mbps":"> 60 Mbps")))));}
function _sumSizes(files){var s=0;for(var i=0;i<files.length;i++){s+=files[i].s;}return s;}

var _movieTitles=["Inception (2010)","Interstellar (2014)","The Matrix (1999)","Oppenheimer (2023)","Dune Part Two (2024)","Your Name (2016)","Pans Labyrinth (2006)","Big Buck Bunny (2008)","Blade Runner 2049 (2017)","The Dark Knight (2008)","Pulp Fiction (1994)","Forrest Gump (1994)","Fight Club (1999)","Gladiator (2000)","Titanic (1997)","Avatar (2009)","Avengers Endgame (2019)","Joker (2019)","Parasite (2019)","La La Land (2016)","Whiplash (2014)","Mad Max Fury Road (2015)","The Revenant (2015)","Gravity (2013)","Arrival (2016)","Ex Machina (2014)","The Martian (2015)","Tenet (2020)","Dunkirk (2017)","1917 (2019)","Jojo Rabbit (2019)","Knives Out (2019)","Get Out (2017)","A Quiet Place (2018)","Dune (2021)","Nope (2022)","Everything Everywhere All at Once Extended Ultimate Remastered Collectors Edition Part Two The Complete Saga Anthology Box Set (2022)","The Batman (2022)","Top Gun Maverick (2022)","John Wick 4 (2023)","Barbie (2023)","Killers of the Flower Moon (2023)","The Holdovers (2023)","Past Lives (2023)","Anatomy of a Fall (2023)","Godzilla Minus One (2023)","Civil War (2024)","Furiosa (2024)","Inside Out 2 (2024)","Deadpool and Wolverine (2024)","Alien Romulus (2024)","The Wild Robot (2024)","Nosferatu (2024)","Wicked (2024)","Anora (2024)","The Brutalist (2024)","Conclave (2024)","Sinners (2025)","Mickey 17 (2025)","F1 (2025)","Superman (2025)","Müller (2020)","The Silence of the Lambs (1991)","Se7en (1995)","Braveheart (1995)","Fargo (1996)","The Big Lebowski (1998)","Saving Private Ryan (1998)","American Beauty (1999)","The Green Mile (1999)","American Psycho (2000)","Memento (2000)","A Beautiful Mind (2001)","LOTR Fellowship (2001)","LOTR Towers (2002)","LOTR Return (2003)","Finding Nemo (2003)","Kill Bill (2003)","Eternal Sunshine (2004)","The Incredibles (2004)","Batman Begins (2005)","V for Vendetta (2005)","The Departed (2006)","Children of Men (2006)","No Country (2007)","Ratatouille (2007)","WALL-E (2008)","Slumdog Millionaire (2008)","Inglourious Basterds (2009)","Up (2009)","The Social Network (2010)","Toy Story 3 (2010)","Black Swan (2010)","Hugo (2011)","Drive (2011)","Django Unchained (2012)","Life of Pi (2012)","Argo (2012)","12 Years a Slave (2013)","Her (2013)","The Wolf of Wall Street (2013)","Birdman (2014)","Gone Girl (2014)","The Grand Budapest Hotel (2014)","Spotlight (2015)","Room (2015)","Moonlight (2016)","Zootopia (2016)","Coco (2017)","Us (2019)","Spider-Man No Way Home (2021)","The Godfather (1972)","Shawshank Redemption (1994)","Pirates Caribbean (2003)","The Dark Knight Rises (2012)","Untitled Project","Alien (1979)"];
var _vtag={"H.264":"x264","HEVC":"x265","AV1":"av1","VP9":"vp9","MPEG-2":"mpeg2"};
var _atag={"AAC":"AAC 2.0","AC3":"DD 5.1","EAC3":"DD+ 5.1","DTS":"DTS 5.1","TrueHD":"TrueHD Atmos","Opus":"Opus 2.0"};
var _movieFiles=[];
(function(){
for(var i=0;i<_movieTitles.length;i++){
var r=_mrnd(),ext=r<0.7?"mkv":(r<0.9?"mp4":(r<0.95?"avi":"webm"));
var rr=_mrnd(),res=rr<0.25?"4K":(rr<0.8?"1080p":(rr<0.95?"720p":"480p"));
var vr=_mrnd(),vc=vr<0.45?"H.264":(vr<0.8?"HEVC":(vr<0.92?"AV1":(vr<0.96?"VP9":"MPEG-2")));
if(ext==="webm"){vc="VP9";} if(ext==="avi"){vc="MPEG-2";}
var ar=_mrnd(),ac=ar<0.4?"AAC":(ar<0.65?"AC3":(ar<0.77?"EAC3":(ar<0.87?"DTS":(ar<0.95?"TrueHD":"Opus"))));
if(ext==="webm"){ac="Opus";}
var al=["English"]; if(_mrnd()<0.3){al.push("German");}
var sl=["English"]; if(_mrnd()<0.12){sl=[];}
var br=res==="4K"?25+_mrnd()*43:(res==="1080p"?5+_mrnd()*20:(res==="720p"?3+_mrnd()*5:1+_mrnd()*2));
var s=res==="4K"?_mbytes(8000000000,25000000000):(res==="1080p"?_mbytes(4000000000,15000000000):(res==="720p"?_mbytes(1000000000,4000000000):_mbytes(500000000,1500000000)));
var w=res==="4K"?3840:(res==="1080p"?1920:(res==="720p"?1280:854));
var h=res==="4K"?_mri(2)?2160:1600:(res==="1080p"?(_mri(2)?1080:800):(res==="720p"?720:480));
var range=res==="4K"?["HDR10","Dolby Vision","HDR10+","HLG"][_mri(4)]:"SDR";
var src=res==="4K"?(ext==="mkv"?"Bluray-2160p":"WEB-DL-2160p"):((ext==="mp4"||ext==="webm")?"WEB-DL":"Bluray-1080p");
var tags=" ["+src+"]["+_atag[ac]+"]["+_vtag[vc]+"]-release1."+ext;
if(al.indexOf("German")>=0){tags=" [German DL]"+tags;}
var p="/SMB/media/movies/"+_movieTitles[i]+" {imdb-"+_mimdb()+"}"+tags;
_movieFiles.push({p:p,s:s,cont:ext.toUpperCase(),res:res,w:w,h:h,vc:vc,ac:ac,al:al,sl:sl,br:Math.round(br*10)/10,tier:_brTier(Math.round(br*10)/10),range:range});
}
_movieFiles[0].al=["English","German"]; _movieFiles[0].alabels=["English","German","German (Forced)"];
_movieFiles[4].al=["English","German"];
_movieFiles[5].al=["Japanese","English"];
_movieFiles[6].al=["Spanish","German"];
_movieFiles[0].sl=["English","German"]; _movieFiles[0].slabels=["English (SRT)","German (PGS, Forced)"];
_movieFiles[7].p=_movieFiles[7].p.replace(" ["," [German DL] [");
_movieFiles[7].al=["English"]; _movieFiles[7].sl=[];
_movieFiles[4].sl=["English"];
_movieFiles[5].sl=["English"]; _movieFiles[5].slabels=["English (ASS)"];
_movieFiles[6].sl=["Spanish"]; _movieFiles[6].slabels=["Spanish (SRT)"];
})();
var _movieSizes={}; for(var _mi=0;_mi<_movieFiles.length;_mi++){_movieSizes[_movieFiles[_mi].p]=_movieFiles[_mi].s;}
var _movieWatchedA=[],_movieWatchedB=[];
var _recSkipA={8:1,24:1,25:1,26:1};
for(var _wi=0;_wi<74;_wi++){if(!_recSkipA[_wi]){_movieWatchedA.push(_movieFiles[_wi].p);}}
for(var _wj=0;_wj<13;_wj++){_movieWatchedB.push(_movieFiles[_wj].p);}
var _movieWatched={}; for(var _wk=0;_wk<_movieWatchedA.length;_wk++){_movieWatched[_movieWatchedA[_wk]]=true;} for(var _wl=0;_wl<_movieWatchedB.length;_wl++){_movieWatched[_movieWatchedB[_wl]]=true;}
var _movieWatchedList=Object.keys(_movieWatched);
var _movieNever=_movieFiles.map(function(f){return f.p;}).filter(function(p){return !_movieWatched[p];});
// External sidecar subtitles (server Health semantics: file on disk, embedded
// tracks in sl do not count). noSub means no sidecar file, independent of sl.
var _movieSubs={};
for(var _si=0;_si<_movieFiles.length;_si++){var _mf=_movieFiles[_si];var _mb=_mf.p.slice(0,_mf.p.lastIndexOf("."));if(_mf.sl.length>0&&_mrnd()<0.85){var _ep=_mb+".en.srt";_movieSubs[_ep]=_mbytes(40000,300000);}if(_mf.al.indexOf("German")>=0&&_mf.sl.length>0&&_mrnd()<0.5){var _dp=_mb+".de.srt";_movieSubs[_dp]=_mbytes(40000,300000);}}
function _sumObj(o){var _t=0;for(var _k in o){if(Object.hasOwn(o,_k)){_t+=o[_k];}}return _t;}
var _movieNoSub=_movieFiles.filter(function(f){var _b=f.p.slice(0,f.p.lastIndexOf("."));return !_movieSubs[_b+".en.srt"]&&!_movieSubs[_b+".de.srt"];}).map(function(f){return f.p;});

var _shows=[["Breaking Bad","2008"],["Friends","1994"],["Planet Earth III","2023"],["Game of Thrones","2011"],["The Expanse","2015"],["The Office","2005"],["Shogun","2024"],["The Last of Us","2023"],["Stranger Things","2016"],["The Witcher","2019"],["Better Call Saul","2015"],["The Mandalorian","2019"],["Loki","2021"],["WandaVision","2021"],["The Boys","2019"],["Fallout","2024"],["House of the Dragon","2022"],["Rings of Power","2022"],["Wednesday","2022"],["The Crown","2016"],["True Detective","2014"],["Fargo","2014"],["Dark","2017"],["Money Heist","2017"],["Squid Game","2021"],["Ted Lasso","2020"],["Succession","2018"],["The Bear","2022"],["Severance","2022"],["Silo","2023"],["Foundation","2021"],["For All Mankind","2019"],["Battlestar Galactica","2004"],["The Wire","2002"],["Sopranos","1999"],["Mad Men","2007"],["Yojimbo","1961"],["Firefly","2002"],["Cowboy Bebop","1998"],["Attack on Titan","2013"]];
var _tvCodecs=["H.264","HEVC","AV1"];
var _tvFiles=[];
var _tvSeasons=0;
var _bbE01=null,_frE01=null,_peE01=null;
(function(){
for(var i=0;i<_shows.length;i++){
var seasons=5+(i%2),vc=_tvCodecs[i%3],ext=(i%5===0)?"mp4":"mkv";
for(var s=1;s<=seasons;s++){_tvSeasons++;
var eps=10+((i*7+s*3)%11),res=s>seasons-2?(i%2?"1080p":"4K"):(i%2?"720p":"1080p");
for(var e=1;e<=eps;e++){
var al=["English"]; if(i%3===0){al.push("German");} if(i%3===1){al.push("Spanish");}
var sl=_mrnd()<0.85?["English"]:[];
var br=res==="4K"?16+_mrnd()*44:(res==="1080p"?4+_mrnd()*21:(res==="720p"?3+_mrnd()*5:1+_mrnd()*2));
var sz=res==="4K"?_mbytes(1000000000,4000000000):(res==="1080p"?_mbytes(500000000,3500000000):(res==="720p"?_mbytes(400000000,1200000000):_mbytes(200000000,600000000)));
var w=res==="4K"?3840:(res==="1080p"?1920:(res==="720p"?1280:854));
var h=res==="4K"?2160:(res==="1080p"?1080:720);
var range=res==="4K"?["HDR10","Dolby Vision"][_mri(2)]:"SDR";
var ac=ext==="mp4"?"AAC":(i%3===0?"AC3":(i%3===1?"AAC":"EAC3"));
var src=res==="4K"?(ext==="mp4"?"WEB-DL-2160p":"Bluray-2160p"):(ext==="mp4"?"WEB-DL":(_mrnd()<0.15?"HDTV":"Bluray-1080p"));
var tags=(al.indexOf("German")>=0?" [German DL]":"")+" ["+src+"]["+_atag[ac]+"]["+_vtag[vc]+"]-scene."+ext;
var p="/SMB/media/tv/"+_shows[i][0]+" ("+_shows[i][1]+")/Season "+_mpad(s)+"/"+_shows[i][0]+" S"+_mpad(s)+"E"+_mpad(e)+tags;
_tvFiles.push({p:p,s:sz,cont:ext.toUpperCase(),res:res,w:w,h:h,vc:vc,ac:ac,al:al,sl:sl,br:Math.round(br*10)/10,tier:_brTier(Math.round(br*10)/10),range:range,show:i});
}}
}
_tvFiles[0].p="/SMB/media/tv/Breaking Bad (2008)/Season 01/Breaking Bad S01E01 {imdb-tt0903747} [German DL] [Bluray-1080p][DD 5.1][x264]-release1.mkv";
_tvFiles[0].cont="MKV"; _tvFiles[0].ac="AC3";
_bbE01=_tvFiles[0].p;
for(var _fi=0;_fi<_tvFiles.length;_fi++){if(_tvFiles[_fi].p.indexOf("/Friends (1994)/")>=0&&_tvFiles[_fi].p.indexOf("S01E01")>=0){_frE01=_tvFiles[_fi].p;break;}}
for(var _pi=0;_pi<_tvFiles.length;_pi++){if(_tvFiles[_pi].p.indexOf("/Planet Earth III (2023)/")>=0&&_tvFiles[_pi].p.indexOf("S01E01")>=0){_peE01=_tvFiles[_pi].p;break;}}
_tvFiles[0].al=["English","German"]; _tvFiles[0].alabels=["English","German"];
var _frIdx=-1; for(var _fj=0;_fj<_tvFiles.length;_fj++){if(_tvFiles[_fj].p===_frE01){_frIdx=_fj;break;}}
if(_frIdx>=0){_tvFiles[_frIdx].al=["English","Spanish"]; _tvFiles[_frIdx].alabels=["English","Spanish"];}
_tvFiles[0].sl=["English"]; _tvFiles[0].slabels=["English (SRT)"];
_tvFiles[1].sl=["German"]; _tvFiles[1].slabels=["German (SRT)"];
if(_frIdx>=0){_tvFiles[_frIdx].sl=["English"]; _tvFiles[_frIdx].slabels=["English (SRT)"];}
var _pex=-1; for(var _px=0;_px<_tvFiles.length;_px++){if(_tvFiles[_px].p===_peE01){_pex=_px;break;}}
if(_pex>=0){_tvFiles[_pex].sl=["English"]; _tvFiles[_pex].slabels=["English (SRT)"];}
})();
var _tvSizes={}; for(var _ti=0;_ti<_tvFiles.length;_ti++){_tvSizes[_tvFiles[_ti].p]=_tvFiles[_ti].s;}
var _tvWatchedA=[],_tvWatchedC=[];
for(var _tk=0;_tk<_tvFiles.length;_tk++){if(_tk%3===0){_tvWatchedA.push(_tvFiles[_tk].p);} if(_tk%11===0){_tvWatchedC.push(_tvFiles[_tk].p);}}
if(_frE01&&_tvWatchedC.indexOf(_frE01)<0){_tvWatchedC.push(_frE01);}
var _tvWatched={}; for(var _tl=0;_tl<_tvWatchedA.length;_tl++){_tvWatched[_tvWatchedA[_tl]]=true;} for(var _tm=0;_tm<_tvWatchedC.length;_tm++){_tvWatched[_tvWatchedC[_tm]]=true;}
var _tvWatchedList=Object.keys(_tvWatched);
var _tvNever=_tvFiles.map(function(f){return f.p;}).filter(function(p){return !_tvWatched[p];});
var _tvSubs={};
for(var _st=0;_st<_tvFiles.length;_st++){var _tf=_tvFiles[_st];var _tb=_tf.p.slice(0,_tf.p.lastIndexOf("."));if(_tf.sl.length>0&&_mrnd()<0.85){var _te=_tb+".en.srt";_tvSubs[_te]=_mbytes(40000,300000);}}
var _tvNoSub=_tvFiles.filter(function(f){var _b=f.p.slice(0,f.p.lastIndexOf("."));return !_tvSubs[_b+".en.srt"];}).map(function(f){return f.p;});
var _tvPathShow={}; for(var _psi=0;_psi<_tvFiles.length;_psi++){_tvPathShow[_tvFiles[_psi].p]=_tvFiles[_psi].show;}
function _tvWatchedShows(list){var _d={};for(var _q=0;_q<list.length;_q++){if(_tvPathShow[list[_q]]!==undefined){_d[_tvPathShow[list[_q]]]=true;}}return Object.keys(_d).length;}

var _albums=[["Pink Floyd","The Wall","FLAC"],["The Beatles","Abbey Road","ALAC"],["Beethoven","Symphonies","WAV"],["Daft Punk","Discovery","FLAC"],["Radiohead","OK Computer","FLAC"],["Kendrick Lamar","DAMN","MP3"],["Taylor Swift","1989","MP3"],["Miles Davis","Kind of Blue","FLAC"],["Nirvana","Nevermind","MP3"],["Queen","Night at the Opera","FLAC"],["ABBA","Gold","MP3"],["Metallica","Black Album","FLAC"],["Billie Eilish","Happier Than Ever","AAC"],["Tame Impala","Currents","Opus"],["Fleetwood Mac","Rumours","FLAC"],["Joy Division","Unknown Pleasures","WavPack"],["Portishead","Dummy","Opus"],["Massive Attack","Mezzanine","AAC"]];
var _songs=["Midnight Sun","Neon Skyline","Paper Boats","Velvet Static","Golden Hour","Rust and Bone","Echo Park","Northern Lights","Slow Parade","Glasshouse","Fever Dream","Low Orbit","Cedar Smoke","Blue Hour","Iron Bloom","Silent Reels","Copper Sky","Night Drive","Amber Waves","Concrete Roses","Salt Air","Mono No Aware","Foxglove","Highwater","Lanterns","Ultraviolet","Daydreamers","Cold Open","Second Act","Encore","Overture","Interlude","B Side","Deep Cut","Title Track","Hidden Track","Bonus Round","Afterglow","Daybreak","Starling"];
var _songExt={"FLAC":"flac","MP3":"mp3","Opus":"opus","AAC":"m4a","ALAC":"m4a","WavPack":"wv","WAV":"wav"};
var _songSize={"FLAC":[25000000,38000000],"MP3":[7000000,11000000],"Opus":[14000000,22000000],"AAC":[6000000,10000000],"ALAC":[22000000,34000000],"WavPack":[28000000,42000000],"WAV":[35000000,50000000]};
var _musicFiles=[];
(function(){
for(var a=0;a<_albums.length;a++){
for(var t=1;t<=25;t++){
var title=_songs[(a*7+t-1)%_songs.length],ext=_songExt[_albums[a][2]],sz=_mbytes(_songSize[_albums[a][2]][0],_songSize[_albums[a][2]][1]);
_musicFiles.push({p:"/SMB/media/music/"+_albums[a][0]+"/"+_albums[a][1]+"/"+_mpad(t)+" "+title+"."+ext,s:sz,codec:_albums[a][2]});
}
}
})();
var _musicSizes={}; for(var _mui=0;_mui<_musicFiles.length;_mui++){_musicSizes[_musicFiles[_mui].p]=_musicFiles[_mui].s;}

var _authors=[["Brandon Sanderson",["Mistborn","Way of Kings","Elantris","Warbreaker","Steelheart"]],["Tolkien",["The Hobbit","LOTR Fellowship","LOTR Towers","LOTR Return","Silmarillion"]],["Asimov",["Foundation","I Robot","Caves of Steel","Naked Sun","Robots of Dawn"]],["Frank Herbert",["Dune","Dune Messiah","Children of Dune","God Emperor","Heretics"]],["Le Guin",["Earthsea Wizard","Tombs of Atuan","Left Hand of Darkness","Dispossessed","Lathe of Heaven"]],["Arthur C. Clarke",["2001","Rendezvous with Rama","Childhoods End","Fountains of Paradise","Songs of Distant Earth"]],["Philip K. Dick",["Do Androids Dream","Man in the High Castle","Ubik","Scanner Darkly","VALIS"]],["Neal Stephenson",["Snow Crash","Cryptonomicon","Anathem","Seveneves","Diamond Age"]],["William Gibson",["Neuromancer","Count Zero","Mona Lisa Overdrive","Pattern Recognition","Peripheral"]],["Iain M. Banks",["Consider Phlebas","Player of Games","Use of Weapons","Excession","Hydrogen Sonata"]],["Alastair Reynolds",["Revelation Space","Chasm City","Redemption Ark","House of Suns","Pushing Ice"]],["Peter F. Hamilton",["Pandoras Star","Judas Unchained","Reality Dysfunction","Neutronium Alchemist","Naked God"]],["Dan Simmons",["Hyperion","Fall of Hyperion","Endymion","Rise of Endymion","Ilium"]],["Orson Scott Card",["Enders Game","Speaker for the Dead","Xenocide","Children of Mind","Enders Shadow"]],["Robert Jordan",["Eye of the World","Great Hunt","Dragon Reborn","Shadow Rising","Fires of Heaven"]],["George R.R. Martin",["Game of Thrones","Clash of Kings","Storm of Swords","Feast for Crows","Dance with Dragons"]],["Patrick Rothfuss",["Name of the Wind","Wise Mans Fear","Slow Regard","Narrow Road","Lightning Tree"]],["N.K. Jemisin",["Fifth Season","Obelisk Gate","Stone Sky","Hundred Thousand Kingdoms","Stone Hunger"]]];
var _bookFmts=["EPUB","PDF","CBZ","MOBI","AZW3"];
var _bookExt={"EPUB":"epub","PDF":"pdf","CBZ":"cbz","MOBI":"mobi","AZW3":"azw3"};
var _bookSize={"EPUB":[800000,6000000],"PDF":[4000000,30000000],"CBZ":[40000000,150000000],"MOBI":[600000,3000000],"AZW3":[800000,4000000]};
var _bookFiles=[];
(function(){
var i=0;
for(var a=0;a<_authors.length;a++){
for(var t=0;t<_authors[a][1].length;t++){
var fmt=_bookFmts[i%5],ext=_bookExt[fmt],sz=_mbytes(_bookSize[fmt][0],_bookSize[fmt][1]);
_bookFiles.push({p:"/SMB/media/books/"+_authors[a][0]+"/"+_authors[a][1][t]+"."+ext,s:sz,fmt:fmt});
i++;
}
}
})();
var _bookSizes={}; for(var _bi=0;_bi<_bookFiles.length;_bi++){_bookSizes[_bookFiles[_bi].p]=_bookFiles[_bi].s;}

function _vDict(files,key){var d={};for(var i=0;i<files.length;i++){var v=files[i][key];var vs=Array.isArray(v)?v:[v];for(var j=0;j<vs.length;j++){d[vs[j]]=(d[vs[j]]||0)+1;}}return d;}
function _vPaths(files,key){var d={};for(var i=0;i<files.length;i++){var v=files[i][key];var vs=Array.isArray(v)?v:[v];for(var j=0;j<vs.length;j++){if(!d[vs[j]]){d[vs[j]]=[];}d[vs[j]].push(files[i].p);}}return d;}
function _vSizes(files,key,sizes){var d={};for(var i=0;i<files.length;i++){var v=files[i][key];var vs=Array.isArray(v)?v:[v];for(var j=0;j<vs.length;j++){d[vs[j]]=(d[vs[j]]||0)+sizes[files[i].p];}}return d;}
function _vLabels(files){var d={};for(var i=0;i<files.length;i++){if(files[i].alabels){d[files[i].p]=files[i].alabels;}}return d;}
function _vDims(files){var d={};for(var i=0;i<files.length;i++){d[files[i].p]=files[i].w+"x"+files[i].h;}return d;}

var _moviesLib=_lib("Movies","movies",{
RootPaths:["/SMB/media/movies"],
VideoSize:_sumSizes(_movieFiles),ImageSize:640000,NfoSize:36000,TrickplaySize:826000000,OtherSize:280000,SubtitleSize:_sumObj(_movieSubs),
VideoFileCount:_movieFiles.length,ImageFileCount:2,NfoFileCount:1,TrickplayFolderCount:115,OtherFileCount:7,SubtitleFileCount:Object.keys(_movieSubs).length,
VideoCodecs:_vDict(_movieFiles,"vc"),
VideoAudioCodecs:_vDict(_movieFiles,"ac"),
ContainerFormats:_vDict(_movieFiles,"cont"),
Resolutions:_vDict(_movieFiles,"res"),
VideoCodecSizes:_vSizes(_movieFiles,"vc",_movieSizes),
VideoAudioCodecSizes:_vSizes(_movieFiles,"ac",_movieSizes),
ContainerSizes:_vSizes(_movieFiles,"cont",_movieSizes),
ResolutionSizes:_vSizes(_movieFiles,"res",_movieSizes),
VideoCodecPaths:_vPaths(_movieFiles,"vc"),
VideoAudioCodecPaths:_vPaths(_movieFiles,"ac"),
ContainerFormatPaths:_vPaths(_movieFiles,"cont"),
ResolutionPaths:_vPaths(_movieFiles,"res"),
ResolutionDimensions:_vDims(_movieFiles),
DynamicRanges:_vDict(_movieFiles,"range"),
DynamicRangeSizes:_vSizes(_movieFiles,"range",_movieSizes),
DynamicRangePaths:_vPaths(_movieFiles,"range"),
VideoBitrateTiers:_vDict(_movieFiles,"tier"),
VideoBitrateTierSizes:_vSizes(_movieFiles,"tier",_movieSizes),
VideoBitrateTierPaths:_vPaths(_movieFiles,"tier"),
VideoBitrates:(function(){var d={};for(var i=0;i<_movieFiles.length;i++){d[_movieFiles[i].p]=_movieFiles[i].br;}return d;})(),
AudioTrackLabels:_vLabels(_movieFiles),
SubtitleTrackLabels:(function(){var d={};for(var i=0;i<_movieFiles.length;i++){if(_movieFiles[i].slabels){d[_movieFiles[i].p]=_movieFiles[i].slabels;}}return d;})(),
AudioLanguages:_vDict(_movieFiles,"al"),
AudioLanguageSizes:_vSizes(_movieFiles,"al",_movieSizes),
AudioLanguagePaths:_vPaths(_movieFiles,"al"),
SubtitleLanguages:_vDict(_movieFiles,"sl"),
SubtitleLanguageSizes:_vSizes(_movieFiles,"sl",_movieSizes),
SubtitleLanguagePaths:_vPaths(_movieFiles,"sl"),
WatchedTiers:{"Watched":_movieWatchedList.length,"Never watched":_movieFiles.length-_movieWatchedList.length},
WatchedTierSizes:(function(){var w=0;for(var i=0;i<_movieWatchedList.length;i++){w+=_movieSizes[_movieWatchedList[i]];}return {"Watched":w,"Never watched":_sumSizes(_movieFiles)-w};})(),
WatchedTierPaths:{"Watched":_movieWatchedList.slice(),"Never watched":_movieNever.slice()},
WatchedByUserPaths:{"Alice":_movieWatchedA.slice(),"Bob":_movieWatchedB.slice()},
WatchedByUserSizes:(function(){var a=0,b=0;for(var i=0;i<_movieWatchedA.length;i++){a+=_movieSizes[_movieWatchedA[i]];}for(var j=0;j<_movieWatchedB.length;j++){b+=_movieSizes[_movieWatchedB[j]];}return {"Alice":a,"Bob":b};})(),
WatchedDetails:{"__plug__":[]},
FileSizes:_movieSizes,
VideosWithoutSubtitles:_movieNoSub.length,VideosWithoutImages:2,VideosWithoutNfo:1,OrphanedMetadataDirectories:2,
VideosWithoutSubtitlesPaths:_movieNoSub.slice(),
VideosWithoutImagesPaths:[_movieFiles[10].p,_movieFiles[11].p],
VideosWithoutNfoPaths:[_movieFiles[11].p],
OrphanedMetadataDirectoriesPaths:["/SMB/media/movies/Old Shorts/","/SMB/media/movies/Abandoned Clips/"]
});
_moviesLib.WatchedDetails={};
_moviesLib.WatchedDetails[_movieFiles[0].p]=[{"Username":"Alice","PlayCount":3,"LastPlayedDate":"2024-03-01T12:00:00Z","Played":true},{"Username":"Bob","PlayCount":1,"LastPlayedDate":"2024-02-14T20:00:00Z","Played":true}];
_moviesLib.WatchedDetails[_movieFiles[1].p]=[{"Username":"Alice","PlayCount":1,"LastPlayedDate":"2024-01-05T21:00:00Z","Played":true}];
_moviesLib.WatchedDetails[_movieFiles[9].p]=[{"Username":"Alice","PlayCount":2,"LastPlayedDate":"2024-05-11T21:30:00Z","Played":true}];

var _tvLib=_lib("TV Shows","tvshows",{
RootPaths:["/SMB/media/tv"],
VideoSize:_sumSizes(_tvFiles),ImageSize:800000,NfoSize:12000,TrickplaySize:_tvSeasons*50000000,OtherSize:150000,SubtitleSize:_sumObj(_tvSubs),
VideoFileCount:_tvFiles.length,ImageFileCount:2,NfoFileCount:1,TrickplayFolderCount:_tvSeasons,OtherFileCount:4,SubtitleFileCount:Object.keys(_tvSubs).length,
VideoCodecs:_vDict(_tvFiles,"vc"),
VideoAudioCodecs:_vDict(_tvFiles,"ac"),
ContainerFormats:_vDict(_tvFiles,"cont"),
Resolutions:_vDict(_tvFiles,"res"),
VideoCodecSizes:_vSizes(_tvFiles,"vc",_tvSizes),
VideoAudioCodecSizes:_vSizes(_tvFiles,"ac",_tvSizes),
ContainerSizes:_vSizes(_tvFiles,"cont",_tvSizes),
ResolutionSizes:_vSizes(_tvFiles,"res",_tvSizes),
VideoCodecPaths:_vPaths(_tvFiles,"vc"),
VideoAudioCodecPaths:_vPaths(_tvFiles,"ac"),
ContainerFormatPaths:_vPaths(_tvFiles,"cont"),
ResolutionPaths:_vPaths(_tvFiles,"res"),
ResolutionDimensions:_vDims(_tvFiles),
DynamicRanges:_vDict(_tvFiles,"range"),
DynamicRangeSizes:_vSizes(_tvFiles,"range",_tvSizes),
DynamicRangePaths:_vPaths(_tvFiles,"range"),
VideoBitrateTiers:_vDict(_tvFiles,"tier"),
VideoBitrateTierSizes:_vSizes(_tvFiles,"tier",_tvSizes),
VideoBitrateTierPaths:_vPaths(_tvFiles,"tier"),
VideoBitrates:(function(){var d={};for(var i=0;i<_tvFiles.length;i++){d[_tvFiles[i].p]=_tvFiles[i].br;}return d;})(),
AudioTrackLabels:_vLabels(_tvFiles),
SubtitleTrackLabels:(function(){var d={};for(var i=0;i<_tvFiles.length;i++){if(_tvFiles[i].slabels){d[_tvFiles[i].p]=_tvFiles[i].slabels;}}return d;})(),
AudioLanguages:_vDict(_tvFiles,"al"),
AudioLanguageSizes:_vSizes(_tvFiles,"al",_tvSizes),
AudioLanguagePaths:_vPaths(_tvFiles,"al"),
SubtitleLanguages:_vDict(_tvFiles,"sl"),
SubtitleLanguageSizes:_vSizes(_tvFiles,"sl",_tvSizes),
SubtitleLanguagePaths:_vPaths(_tvFiles,"sl"),
WatchedTiers:{"Watched":_tvWatchedList.length,"Never watched":_tvFiles.length-_tvWatchedList.length},
WatchedTierSizes:(function(){var w=0;for(var i=0;i<_tvWatchedList.length;i++){w+=_tvSizes[_tvWatchedList[i]];}return {"Watched":w,"Never watched":_sumSizes(_tvFiles)-w};})(),
WatchedTierPaths:{"Watched":_tvWatchedList.slice(),"Never watched":_tvNever.slice()},
WatchedByUserPaths:{"Alice":_tvWatchedA.slice(),"Bob":_tvWatchedC.slice()},
WatchedByUserSizes:(function(){var a=0,b=0;for(var i=0;i<_tvWatchedA.length;i++){a+=_tvSizes[_tvWatchedA[i]];}for(var j=0;j<_tvWatchedC.length;j++){b+=_tvSizes[_tvWatchedC[j]];}return {"Alice":a,"Bob":b};})(),
WatchedDetails:{},
FileSizes:_tvSizes,
VideosWithoutSubtitles:_tvNoSub.length,VideosWithoutImages:2,VideosWithoutNfo:1,OrphanedMetadataDirectories:2,
VideosWithoutSubtitlesPaths:_tvNoSub.slice(),
VideosWithoutImagesPaths:[_tvFiles[7].p,_tvFiles[20].p],
VideosWithoutNfoPaths:[_tvFiles[9].p],
OrphanedMetadataDirectoriesPaths:["/SMB/media/tv/Cancelled Show/","/SMB/media/tv/Pilot Season/"]
});
_tvLib.WatchedDetails={};
_tvLib.WatchedDetails[_bbE01]=[{"Username":"Alice","PlayCount":2,"LastPlayedDate":"2024-04-02T19:00:00Z","Played":true}];
_tvLib.WatchedDetails[_frE01]=[{"Username":"Bob","PlayCount":1,"LastPlayedDate":"2024-05-11T21:30:00Z","Played":true}];

var _musicLib=_lib("Music","music",{
RootPaths:["/SMB/media/music"],AudioSize:_sumSizes(_musicFiles),ImageSize:0,NfoSize:8000,OtherSize:0,
AudioFileCount:_musicFiles.length,ImageFileCount:0,NfoFileCount:2,OtherFileCount:0,
MusicAudioCodecs:_vDict(_musicFiles,"codec"),
MusicAudioCodecSizes:_vSizes(_musicFiles,"codec",_musicSizes),
MusicAudioCodecPaths:_vPaths(_musicFiles,"codec"),
FileSizes:_musicSizes
});

var _booksLib=_lib("Books","books",{
RootPaths:["/SMB/media/books"],BookSize:_sumSizes(_bookFiles),ImageSize:0,
BookFileCount:_bookFiles.length,ImageFileCount:0,
BookFormats:_vDict(_bookFiles,"fmt"),
BookFormatSizes:_vSizes(_bookFiles,"fmt",_bookSizes),
BookFormatPaths:_vPaths(_bookFiles,"fmt"),
FileSizes:_bookSizes
});

var MOCK_STATISTICS={
ScanTimestamp:new Date(Date.now()-300000).toISOString(),
LibraryOrder:["Movies","TV Shows","Music","Books"],
Movies:[_moviesLib],TvShows:[_tvLib],Music:[_musicLib],Books:[_booksLib],Other:[],
TotalMovieVideoSize:_moviesLib.VideoSize,
TotalTvShowVideoSize:_tvLib.VideoSize,
TotalMusicAudioSize:_musicLib.AudioSize,
TotalBookSize:_booksLib.BookSize,
TotalBookFileCount:_booksLib.BookFileCount,
TotalBookFormats:_booksLib.BookFormats,
TotalTrickplaySize:_moviesLib.TrickplaySize+_tvLib.TrickplaySize+_musicLib.TrickplaySize+_booksLib.TrickplaySize,
TotalSubtitleSize:_moviesLib.SubtitleSize+_tvLib.SubtitleSize+_musicLib.SubtitleSize+_booksLib.SubtitleSize,
TotalImageSize:_moviesLib.ImageSize+_tvLib.ImageSize+_musicLib.ImageSize+_booksLib.ImageSize,
TotalNfoSize:_moviesLib.NfoSize+_tvLib.NfoSize+_musicLib.NfoSize+_booksLib.NfoSize,
TotalVideoFileCount:_moviesLib.VideoFileCount+_tvLib.VideoFileCount+_musicLib.VideoFileCount+_booksLib.VideoFileCount,
TotalAudioFileCount:_musicLib.AudioFileCount,
MovieRootPaths:["/SMB/media/movies"],TvShowRootPaths:["/SMB/media/tv"],MusicRootPaths:["/SMB/media/music"],BookRootPaths:["/SMB/media/books"],OtherRootPaths:[]
};

var _allSizedFiles=(function(){var a=[];[_moviesLib,_tvLib,_musicLib,_booksLib].forEach(function(l){Object.keys(l.FileSizes||{}).forEach(function(p){a.push({p:p,s:l.FileSizes[p],lib:l.LibraryName,ct:l.CollectionType});});});a.sort(function(x,y){return y.s-x.s;});return a;})();
function _findSize(sub){for(var i=0;i<_allSizedFiles.length;i++){if(_allSizedFiles[i].p.includes(sub)){return _allSizedFiles[i];}}return null;}
function _baseName(p){var b=p.split("/").pop();var d=b.lastIndexOf(".");b=d>0?b.substring(0,d):b;return b.replace(/ \{imdb-[^}]*\}/,"").replace(/\[[^\]]*\]/g,"").replace(/-(release\d*|scene)$/,"").replace(/\s+/g," ").replace(/ $/,"");}

var MOCK_CONFIG={
ExcludedLibraries:"",OrphanMinAgeDays:0,
TrickplayTaskMode:"DryRun",EmptyMediaFolderTaskMode:"Activate",
OrphanedSubtitleTaskMode:"DryRun",LinkRepairTaskMode:"Deactivate",
UseTrash:true,TrashFolderPath:".jellyfin-trash",TrashRetentionDays:30,
Language:"en",PluginLogLevel:"INFO",
RadarrInstances:[{Name:"Radarr Main",Url:"http://192.168.1.100:7878",ApiKey:"demo-key-1234",Libraries:"Movies"},{Name:"Radarr 4K",Url:"http://192.168.1.100:7879",ApiKey:"demo-key-5678",Libraries:"Movies"}],
SonarrInstances:[{Name:"Sonarr",Url:"http://192.168.1.100:8989",ApiKey:"demo-key-abcd",Libraries:"TV Shows"},{Name:"Sonarr Anime",Url:"http://192.168.1.100:8990",ApiKey:"demo-key-efgh",Libraries:""}],
SeerrUrl:"http://192.168.1.100:5055",SeerrApiKey:"demo-seerr-key",
SeerrCleanupTaskMode:"DryRun",SeerrCleanupAgeDays:365,
RecommendationsTaskMode:"Activate",DiscoveryUserAccessEnabled:true,ConfigVersion:3
};

var MOCK_CLEANUP_STATS={TotalBytesFreed:8589934592,TotalItemsDeleted:247,LastCleanupTimestamp:new Date(Date.now()-86400000).toISOString()};
var MOCK_HISTORY=(function(){var s=[],now=Date.now(),ev=_moviesLib.VideoFileCount+_tvLib.VideoFileCount,ea=_musicLib.AudioFileCount,es=_moviesLib.SubtitleSize+_tvLib.SubtitleSize+_musicLib.SubtitleSize+_booksLib.SubtitleSize,eo=_moviesLib.OtherSize+_tvLib.OtherSize+_musicLib.OtherSize+_booksLib.OtherSize,ei=_moviesLib.ImageSize+_tvLib.ImageSize+_musicLib.ImageSize+_booksLib.ImageSize,en=_moviesLib.NfoSize+_tvLib.NfoSize+_musicLib.NfoSize+_booksLib.NfoSize;for(var i=30;i>=0;i--){var d=new Date(now-i*86400000);s.push({Timestamp:d.toISOString(),TotalVideoSize:Math.round((_moviesLib.VideoSize+_tvLib.VideoSize)*(0.7+0.3*(30-i)/30)),TotalAudioSize:Math.round(_musicLib.AudioSize*(0.7+0.3*(30-i)/30)),TotalSubtitleSize:Math.round(es*(0.7+0.3*(30-i)/30)),TotalImageSize:Math.round(ei*(0.7+0.3*(30-i)/30)),TotalNfoSize:Math.round(en*(0.7+0.3*(30-i)/30)),TotalTrickplaySize:Math.round((_moviesLib.TrickplaySize+_tvLib.TrickplaySize)*(0.7+0.3*(30-i)/30)),TotalOtherSize:Math.round(eo*(0.7+0.3*(30-i)/30)),TotalVideoFileCount:Math.round(ev*(0.7+0.3*(30-i)/30)),TotalAudioFileCount:Math.round(ea*(0.7+0.3*(30-i)/30)),TotalSize:Math.round((_moviesLib.TotalSize+_tvLib.TotalSize+_musicLib.TotalSize+_booksLib.TotalSize)*(0.7+0.3*(30-i)/30)),LibraryCount:4});}return s;})();

var MOCK_TRASH_CONTENTS={RetentionDays:30,Libraries:[
{LibraryName:"Movies",Items:[{OriginalName:"Old Movie (1995)",Size:4294967296,TrashedDate:new Date(Date.now()-172800000).toISOString(),PurgeDate:new Date(Date.now()+2419200000).toISOString(),IsDirectory:true},{OriginalName:"Duplicate.mkv",Size:2147483648,TrashedDate:new Date(Date.now()-604800000).toISOString(),PurgeDate:new Date(Date.now()+1987200000).toISOString(),IsDirectory:false}]},
{LibraryName:"TV Shows",Items:[{OriginalName:"Cancelled Show",Size:10737418240,TrashedDate:new Date(Date.now()-86400000).toISOString(),PurgeDate:new Date(Date.now()+2505600000).toISOString(),IsDirectory:true}]}
]};
var MOCK_TRASH_FOLDERS={Paths:["/SMB/media/movies/.jellyfin-trash","/SMB/media/tv/.jellyfin-trash"]};
var MOCK_TRASH_FOLDERS_FOR_PATH=function(trashPath){
    if(!trashPath)return{Paths:[],IsAbsolute:false};
    var isAbs=/^(\/|[A-Za-z]:[\\/]|\\\\)/.test(trashPath);
    if(isAbs)return{Paths:[trashPath],IsAbsolute:true};
    var libs=["/SMB/media/movies","/SMB/media/tv"];
    var paths=libs.map(function(l){return l+"/"+trashPath;});
    return{Paths:paths,IsAbsolute:false};
};

var MOCK_ARR_COMPARE={
InBoth:["Inception (2010)","Interstellar (2014)","Dune Part Two (2024)","Oppenheimer (2023)"],
InArrOnly:["Edge of Tomorrow (2014)"],
InArrOnlyMissing:["Heat (1995)","Prisoners (2013)"],
InJellyfinOnly:["Pans Labyrinth (2006)","Your Name (2016)"]
};

var _longLogPath="/SMB/media/movies/Everything Everywhere All at Once Extended Ultimate Collectors Edition Part Two (2022) {imdb-tt9999999} [Bluray-1080p][DD 5.1][x264]-release1/Everything Everywhere All at Once Extended Ultimate Collectors Edition Part Two (2022) {imdb-tt9999999} [Bluray-1080p][DD 5.1][x264]-release1.mkv";
var MOCK_LOGS={Entries:(function(){var e=[],src=["MediaStatistics","TrickplayCleaner","EmptyFolderCleaner","SubtitleCleaner","LinkRepair","TrashService","ArrIntegration"],lvl=["INFO","INFO","INFO","DEBUG","WARN","ERROR","INFO","INFO","DEBUG","INFO"],msg=["Statistics scan started for 4 libraries","Scanning: Movies ("+_moviesLib.VideoFileCount+" video files)","Scanning: TV Shows ("+_tvLib.VideoFileCount+" video files)","Processing: "+_movieFiles[0].p,"Trickplay folder has no matching video: /SMB/media/movies/Old Shorts/.trickplay","Failed to access: /SMB/media/restricted - Access denied","Scan completed in 14.2s - "+(_moviesLib.VideoFileCount+_tvLib.VideoFileCount)+" videos, "+_musicLib.AudioFileCount+" audio files","Empty folder cleanup: 3 folders deleted, 45.2 MB freed","Checking subtitle: "+_longLogPath,"Link repair: 2 files repaired, 0 errors"],now=Date.now();for(var i=0;i<msg.length;i++){e.push({Timestamp:new Date(now-(msg.length-i)*60000).toISOString(),Level:lvl[i],Source:src[i%src.length],Message:msg[i],Exception:lvl[i]==="ERROR"?"System.UnauthorizedAccessException: Access denied.\n   at System.IO.Directory.InternalEnumerateEntries(...)":null});}return e;})(),TotalCount:10};

var MOCK_GROWTH_TIMELINE=(function(){
var raw=[],start=Date.UTC(2016,5,1),dayMs=86400000,now=Date.now(),cs=0,cf=0,seed=42;
function rnd(){seed=(seed*16807)%2147483647;return(seed-1)/2147483646;}
for(var t=start;t<=now;t+=dayMs){var d=new Date(t),yi=d.getUTCFullYear()-2016;
if(rnd()<0.18){var bf=Math.floor(1+rnd()*(4+yi*2));var avg=(1.5+yi*0.5+rnd()*1.5)*1073741824;
cs+=Math.floor(bf*avg);cf+=bf;}
raw.push({date:new Date(t).toISOString(),cumulativeSize:cs,cumulativeFileCount:cf});}
var dp=[];for(var i=0;i<raw.length;i++){if(i===0||i===raw.length-1||raw[i].cumulativeSize!==raw[i-1].cumulativeSize||raw[i].cumulativeFileCount!==raw[i-1].cumulativeFileCount)dp.push(raw[i]);}
return{granularity:"daily",earliestFileDate:new Date(start).toISOString(),computedAt:new Date(now).toISOString(),totalDirectoriesScanned:4,dataPoints:dp};})();

var MOCK_LIBRARY_INSIGHTS=(function(){
var top=_allSizedFiles.slice(0,8).map(function(f,ix){return{Name:_baseName(f.p),Size:f.s,CreatedUtc:new Date(Date.UTC(2018+(ix%6),ix%12,3+(ix*7)%25,10,0,0)).toISOString(),ModifiedUtc:new Date(Date.now()-(ix*37+11)*86400000).toISOString(),LibraryName:f.lib,CollectionType:f.ct,ChangeType:""};});
var total=0;for(var i=0;i<top.length;i++){total+=top[i].Size;}
function recent(sub,days,type){var f=_findSize(sub);if(!f){return null;}return{Name:_baseName(f.p),Size:f.s,CreatedUtc:new Date(Date.now()-days*86400000).toISOString(),ModifiedUtc:new Date(Date.now()-days*86400000).toISOString(),LibraryName:f.lib,CollectionType:f.ct,ChangeType:type};}
return{Largest:top,LargestTotalSize:total,
Recent:[recent("Dune Part Two",3,"added"),recent("Breaking Bad",5,"added"),recent("Oppenheimer (2023)",7,"changed"),recent("Your Name (2016)",10,"changed"),recent("Planet Earth III",12,"added"),recent("Pans Labyrinth (2006)",15,"added")].filter(function(r){return !!r;}),
RecentTotalCount:6,
LibrarySizes:{"Movies":_moviesLib.TotalSize,"TV Shows":_tvLib.TotalSize,"Music":_musicLib.TotalSize,"Books":_booksLib.TotalSize},
ComputedAtUtc:new Date().toISOString()};})();

var MOCK_LIBRARIES=[{name:"Movies",collectionType:"movies"},{name:"TV Shows",collectionType:"tvshows"},{name:"Music",collectionType:"music"},{name:"Books",collectionType:"books"}];

var MOCK_LIBRARY_PATHS={libraryPaths:[
{name:"Movies",path:"/SMB/media/movies"},
{name:"TV Shows",path:"/SMB/media/tv"},
{name:"Music",path:"/SMB/media/music"},
{name:"Books",path:"/SMB/media/books"}
]};

var MOCK_BROWSE_FOLDERS=(function(){
    var tree={
        "/":["SMB","home","mnt","opt","var"],
        "/SMB":["media"],
        "/SMB/media":["movies","tv","music","books","backups","downloads"],
        "/SMB/media/movies":[".jellyfin-trash","Action","Comedy","Sci-Fi"],
        "/SMB/media/movies/.jellyfin-trash":[],
        "/SMB/media/movies/Action":[],
        "/SMB/media/movies/Comedy":[],
        "/SMB/media/movies/Sci-Fi":[],
        "/SMB/media/tv":[".jellyfin-trash","Drama","Animation"],
        "/SMB/media/tv/.jellyfin-trash":[],
        "/SMB/media/tv/Drama":[],
        "/SMB/media/tv/Animation":[],
        "/SMB/media/music":["FLAC","MP3","Podcasts"],
        "/SMB/media/music/FLAC":[],
        "/SMB/media/music/MP3":[],
        "/SMB/media/music/Podcasts":[],
        "/SMB/media/books":[],
        "/SMB/media/backups":[],
        "/SMB/media/downloads":["incomplete","complete"],
        "/SMB/media/downloads/incomplete":[],
        "/SMB/media/downloads/complete":[],
        "/home":["jellyfin"],
        "/home/jellyfin":[],
        "/mnt":["nas-share","usb-drive"],
        "/mnt/nas-share":[],
        "/mnt/usb-drive":[]
    };
function getParent(p){if(!p||p==="/"){return null;}var parts=p.replace(/\/$/,"").split("/");parts.pop();return parts.length<=1?"/":parts.join("/");}
return function(path){
if(!path){return{CurrentPath:null,ParentPath:null,CanGoUp:false,Directories:[{Name:"/",Path:"/",HasChildren:true}]};}
var parentPath=getParent(path);
var children=tree[path];
if(!children){return{CurrentPath:path,ParentPath:parentPath,CanGoUp:parentPath!==null,Directories:[],Error:"Directory does not exist."};}
var dirs=children.map(function(name){var full=path==="/"?"/"+name:path+"/"+name;return{Name:name,Path:full,HasChildren:!!(tree[full]&&tree[full].length>0)};});
return{CurrentPath:path,ParentPath:parentPath,CanGoUp:parentPath!==null,Directories:dirs};
};
})();

var _uid1="a1b2c3d4-e5f6-7890-abcd-ef1234567890",_uid2="b2c3d4e5-f6a7-8901-bcde-f12345678901";

var MOCK_DISCOVERY=[
{UserId:_uid1,UserName:"Alice",GeneratedAt:new Date(Date.now()-3600000).toISOString(),Recommendations:[
{TmdbId:693134,MediaType:"movie",Title:"Dune: Part Two",Year:2024,Score:0.92,Reason:"Popular among similar viewers",ReasonKey:"reasonCollaborative",Genres:["Sci-Fi","Adventure"],TmdbRating:8.2,PosterPath:"/8b8R8l88Qje9dn9OE8PY05Nez7.jpg",Overview:"Follow the mythic journey of Paul Atreides as he unites with Chani and the Fremen while on a warpath of revenge against the conspirators who destroyed his family.",AlreadyRequested:false},
{TmdbId:746036,MediaType:"movie",Title:"The Fall Guy",Year:2024,Score:0.88,Reason:"Trending — new and highly rated",ReasonKey:"reasonTrending",Genres:["Action","Comedy"],TmdbRating:7.3,PosterPath:"/tSz1qsmSJon0rqjHBxXZmrotuse.jpg",Overview:"A down-and-out stuntman must track down a missing movie star, solve a conspiracy, and try to win back the love of his life while still doing his day job.",AlreadyRequested:false},
{TmdbId:108545,MediaType:"tv",Title:"3 Body Problem",Year:2024,Score:0.86,Reason:"Because you like Sci-Fi",ReasonKey:"reasonGenre",RelatedInfo:"Sci-Fi",Genres:["Sci-Fi","Drama","Mystery"],TmdbRating:7.6,PosterPath:"/sg4xJGSJKjGZ3YKDDmzqY9cHCOB.jpg",Overview:"A fateful decision made in 1960s China reverberates across space and time to a group of brilliant scientists in the present day.",AlreadyRequested:false},
{TmdbId:786892,MediaType:"movie",Title:"Furiosa: A Mad Max Saga",Year:2024,Score:0.84,Reason:"With actors you like",ReasonKey:"reasonPeople",Genres:["Action","Sci-Fi","Adventure"],TmdbRating:7.5,PosterPath:"/iADOJ8Zymht2JPMoy3R7xceZprc.jpg",Overview:"As the world fell, young Furiosa is snatched from the Green Place of Many Mothers and falls into the hands of a great Biker Horde led by the Warlord Dementus.",AlreadyRequested:false},
{TmdbId:126308,MediaType:"tv",Title:"Sh\u014dgun",Year:2024,Score:0.81,Reason:"Highly rated",ReasonKey:"reasonHighlyRated",Genres:["Drama","War"],TmdbRating:8.7,PosterPath:"/7O4iVfOMQmdCSxhOg1WnzG1AgYT.jpg",Overview:"In Japan in the year 1600, at the dawn of a century-defining civil war, Lord Yoshii Toranaga is fighting for his life as his enemies on the Council of Regents unite against him.",AlreadyRequested:false},
{TmdbId:1184918,MediaType:"movie",Title:"The Wild Robot",Year:2024,Score:0.78,Reason:"New and highly rated",ReasonKey:"reasonTrending",Genres:["Animation","Sci-Fi","Family"],TmdbRating:8.4,PosterPath:"/wTnV3PCVW5O92JMrFvvrRcV39RU.jpg",Overview:"After a shipwreck, an intelligent robot called Roz is stranded on an uninhabited island. To survive the harsh environment, Roz bonds with the island's animals.",AlreadyRequested:false}
]},
{UserId:_uid2,UserName:"Bob",GeneratedAt:new Date(Date.now()-3600000).toISOString(),Recommendations:[
{TmdbId:840430,MediaType:"movie",Title:"The Holdovers",Year:2023,Score:0.90,Reason:"From a director you like",ReasonKey:"reasonStudioNamed",RelatedInfo:"Alexander Payne",Genres:["Comedy","Drama"],TmdbRating:7.9,PosterPath:"/VHSzNBTwxV8vh7wylo7O9CLdac.jpg",Overview:"A curmudgeonly instructor at a New England prep school is forced to remain on campus during Christmas break to babysit a handful of students with nowhere to go.",AlreadyRequested:false},
{TmdbId:203737,MediaType:"tv",Title:"Ripley",Year:2024,Score:0.87,Reason:"Because you watched {0}",ReasonKey:"reasonGenreAndCollab",RelatedInfo:"Drama",Genres:["Crime","Drama","Thriller"],TmdbRating:7.8,PosterPath:"/bkZpMfsMwfqRVEwfUn8i3qi2NDE.jpg",Overview:"Tom Ripley, a grifter in early 1960s New York, is hired by a wealthy man to travel to Italy to try to convince his vagabond son to return home.",AlreadyRequested:false},
{TmdbId:792307,MediaType:"movie",Title:"Poor Things",Year:2023,Score:0.85,Reason:"Popular among similar viewers",ReasonKey:"reasonCollaborative",Genres:["Comedy","Drama","Thriller"],TmdbRating:7.8,PosterPath:"/kCGlIMHnOm8JPXq3rXM6c5wMxcT.jpg",Overview:"Brought back to life by an unorthodox scientist, a young woman runs off with a debauched lawyer on a whirlwind adventure across the continents.",AlreadyRequested:false},
{TmdbId:208942,MediaType:"tv",Title:"A Gentleman in Moscow",Year:2024,Score:0.82,Reason:"Matches your top genres",ReasonKey:"reasonGenre",RelatedInfo:"Drama",Genres:["Drama","History"],TmdbRating:7.1,PosterPath:"/aBU2mlyZT5BzvCkXXUewH7wfhkK.jpg",Overview:"A Count is sentenced to house arrest in a grand Moscow hotel for decades. As Russia undergoes decades of tumultuous change, he must find purpose in an ever-shrinking world.",AlreadyRequested:false},
{TmdbId:467244,MediaType:"movie",Title:"The Zone of Interest",Year:2023,Score:0.79,Reason:"Highly rated drama",ReasonKey:"reasonHighlyRated",Genres:["Drama","History","War"],TmdbRating:7.1,PosterPath:"/hUu9zyZmDd8VZegKi1iK1Vk0RYS.jpg",Overview:"The commandant of Auschwitz, Rudolf H\u00f6ss, and his wife Hedwig, strive to build a dream life for their family in a house and garden next to the camp.",AlreadyRequested:false}
]}
];

var MOCK_SEERR_SERVICES_RADARR=[{
id:1,name:"Radarr Main",isDefault:true,is4k:false,
activeProfileId:4,activeDirectory:"/SMB/media/movies",
profiles:[{id:4,name:"HD-1080p"},{id:6,name:"Ultra-HD"}],
rootFolders:[{id:1,path:"/SMB/media/movies"}]
}];

var MOCK_SEERR_SERVICES_SONARR=[];

var MOCK_RECOMMENDATIONS=[
{UserId:_uid1,UserName:"Alice",Recommendations:[
{Name:"Blade Runner 2049",Score:0.94,ItemType:"Movie",Genres:["Sci-Fi","Thriller"],Year:2017,Reason:"Because you watched {0}",ReasonKey:"recsBecauseWatched",RelatedItemName:"Blade Runner"},
{Name:"Ex Machina",Score:0.91,ItemType:"Movie",Genres:["Sci-Fi","Thriller"],Year:2014,Reason:"Matches your top genres",ReasonKey:"recsGenreMatch"},
{Name:"The Martian",Score:0.85,ItemType:"Movie",Genres:["Sci-Fi","Adventure"],Year:2015,Reason:"Directed by Ridley Scott",ReasonKey:"recsDirectorMatch"},
{Name:"Arrival",Score:0.85,ItemType:"Movie",Genres:["Sci-Fi","Drama"],Year:2016,Reason:"Because you watched {0}",ReasonKey:"recsBecauseWatched",RelatedItemName:"Interstellar"},
{Name:"Edge of Tomorrow",Score:0.82,ItemType:"Movie",Genres:["Sci-Fi","Action"],Year:2014,Reason:"Popular in your favorite genres",ReasonKey:"recsPopularGenre"},
{Name:"Westworld",Score:0.79,ItemType:"Series",Genres:["Sci-Fi","Drama"],Year:2016,Reason:"Matches your top genres",ReasonKey:"recsGenreMatch"}
]},
{UserId:_uid2,UserName:"Bob",Recommendations:[
{Name:"The Grand Budapest Hotel",Score:0.92,ItemType:"Movie",Genres:["Comedy","Drama"],Year:2014,Reason:"Directed by Wes Anderson",ReasonKey:"recsDirectorMatch"},
{Name:"Lady Bird",Score:0.88,ItemType:"Movie",Genres:["Comedy","Drama"],Year:2017,Reason:"Matches your top genres",ReasonKey:"recsGenreMatch"},
{Name:"Parasite",Score:0.86,ItemType:"Movie",Genres:["Drama","Thriller"],Year:2019,Reason:"Highly rated drama",ReasonKey:"recsHighlyRated"},
{Name:"Jojo Rabbit",Score:0.83,ItemType:"Movie",Genres:["Comedy","Drama"],Year:2019,Reason:"Because you watched {0}",ReasonKey:"recsBecauseWatched",RelatedItemName:"The Grand Budapest Hotel"},
{Name:"The Office",Score:0.80,ItemType:"Series",Genres:["Comedy"],Year:2005,Reason:"Popular in Comedy",ReasonKey:"recsPopularGenre"}
]}
];

var MOCK_WATCH_PROFILES={};
var MOCK_ENSEMBLE_DIAGNOSTICS={Available:true,IsPerUser:false,Alpha:0.62,NeuralBeta:0.28,QualityGateFrozen:false,SigmoidMidpointOffset:-6,EffectiveSigmoidMidpoint:44,Trend:"Improving",TrainingExampleCount:342,MetricsHistoryCount:10,AlphaMin:0.15,AlphaMax:0.85,NeuralEnabled:true};
var MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER={};
MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[_uid1]={Available:true,IsPerUser:true,UserName:"Alice",Alpha:0.68,NeuralBeta:0.31,QualityGateFrozen:false,SigmoidMidpointOffset:-6,EffectiveSigmoidMidpoint:44,Trend:"Improving",TrainingExampleCount:412,MetricsHistoryCount:10,AlphaMin:0.15,AlphaMax:0.85,NeuralEnabled:true};
MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[_uid2]=Object.assign({},MOCK_ENSEMBLE_DIAGNOSTICS,{UserName:"Bob"});
MOCK_WATCH_PROFILES[_uid1]={WatchedMovieCount:_movieWatchedA.length,WatchedEpisodeCount:_tvWatchedA.length,WatchedSeriesCount:_tvWatchedShows(_tvWatchedA),FavoriteCount:12,GenreDistribution:{"Sci-Fi":60,"Drama":40,"Action":30,"Thriller":18,"Adventure":12,"Comedy":8,"Horror":5}};
MOCK_WATCH_PROFILES[_uid2]={WatchedMovieCount:_movieWatchedB.length,WatchedEpisodeCount:_tvWatchedC.length,WatchedSeriesCount:_tvWatchedShows(_tvWatchedC),FavoriteCount:4,GenreDistribution:{"Drama":14,"Comedy":12,"Sci-Fi":8,"Thriller":5,"Adventure":3,"War":2}};
MOCK_USER_ACTIVITY={};
MOCK_USER_ACTIVITY[_uid1]=[
{ItemName:"Interstellar",ItemType:"Movie",TotalPlayCount:3,MostRecentWatch:new Date(Date.now()-2*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Dune Part Two",ItemType:"Movie",TotalPlayCount:2,MostRecentWatch:new Date(Date.now()-4*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Breaking Bad",ItemType:"Series",TotalPlayCount:120,MostRecentWatch:new Date(Date.now()-5*86400000).toISOString(),AverageCompletionPercent:95},
{ItemName:"Inception",ItemType:"Movie",TotalPlayCount:4,MostRecentWatch:new Date(Date.now()-25*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"The Dark Knight",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-8*86400000).toISOString(),AverageCompletionPercent:100}
];
MOCK_USER_ACTIVITY[_uid2]=[
{ItemName:"Pans Labyrinth",ItemType:"Movie",TotalPlayCount:2,MostRecentWatch:new Date(Date.now()-86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Friends",ItemType:"Series",TotalPlayCount:30,MostRecentWatch:new Date(Date.now()-3*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"The Matrix",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-6*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Big Buck Bunny",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-14*86400000).toISOString(),AverageCompletionPercent:100}
];

var ApiClient={
accessToken:function(){return"mock-demo-token";},
getUrl:function(p){return"mock://"+p;},
ajax:function(opts){var url=opts.url||"",method=(opts.type||"GET").toUpperCase();
return new Promise(function(resolve){setTimeout(function(){
if(url.includes("Translations")){var _lang=MOCK_CONFIG.Language||'en';fetch('i18n/'+_lang+'.json').then(function(r){return r.json();}).then(function(t){MOCK_TRANSLATIONS=t;resolve(t);}).catch(function(){MOCK_TRANSLATIONS={};resolve(MOCK_TRANSLATIONS);});}
else if(url.includes("Statistics/Latest"))resolve(structuredClone(MOCK_STATISTICS));
else if(url.includes("GrowthTimeline"))resolve(structuredClone(MOCK_GROWTH_TIMELINE));
else if(url.includes("Statistics/History"))resolve(MOCK_HISTORY);
else if(url.includes("Statistics")&&url.includes("forceRefresh"))resolve(structuredClone(MOCK_STATISTICS));
else if(url.includes("CleanupStatistics"))resolve(MOCK_CLEANUP_STATS);
else if(url.includes("Configuration/LogLevel")&&method==="PUT"){try{var b=JSON.parse(opts.data);if(b.PluginLogLevel)MOCK_CONFIG.PluginLogLevel=b.PluginLogLevel;}catch(e){}resolve({message:"Log level updated.",pluginLogLevel:MOCK_CONFIG.PluginLogLevel});}
else if(url.includes("Configuration/Libraries")&&!url.includes("LibraryPaths"))resolve({libraries:structuredClone(MOCK_LIBRARIES)});
else if(url.includes("Configuration/LibraryPaths"))resolve(structuredClone(MOCK_LIBRARY_PATHS));
else if(url.includes("Configuration/BrowseFolders")||url.includes("BrowseFolders")){var bp=null;var bm=url.match(/[?&]path=([^&]*)/);if(bm){bp=decodeURIComponent(bm[1]);}resolve(structuredClone(MOCK_BROWSE_FOLDERS(bp)));}
else if(url.includes("Configuration")&&(method==="POST"||method==="PUT")){try{Object.assign(MOCK_CONFIG,JSON.parse(opts.data));}catch(e){}resolve({});}
else if(url.includes("Configuration"))resolve(structuredClone(MOCK_CONFIG));
else if(url.includes("Trash/Contents"))resolve(MOCK_TRASH_CONTENTS);
    else if(url.includes("Trash/CheckAccess")&&method==="POST"){resolve({AllAccessible:true,Results:[{Path:"/SMB/media/movies/.jellyfin-trash",Exists:false,CanRead:true,CanWrite:true,HasFullAccess:true,ErrorMessage:null}]});}
    else if(url.includes("Trash/Relocate")&&method==="POST"){resolve({Moved:3,Failed:0});}
    else if(url.includes("Trash/FoldersForPath")&&method==="POST"){try{var fpBody=JSON.parse(opts.data);resolve(structuredClone(MOCK_TRASH_FOLDERS_FOR_PATH(fpBody.TrashFolderPath)));}catch(e){resolve({Paths:[],IsAbsolute:false});}}
    else if(url.includes("Trash/Folders")&&method==="DELETE")resolve({deleted:2,failed:0});
    else if(url.includes("Trash/Folders"))resolve(MOCK_TRASH_FOLDERS);
else if(url.includes("Trash/Summary"))resolve({TotalSize:17179869184,TotalItems:3});
else if(url.includes("Discovery/Services/radarr"))resolve(structuredClone(MOCK_SEERR_SERVICES_RADARR));
else if(url.includes("Discovery/Services/sonarr"))resolve(structuredClone(MOCK_SEERR_SERVICES_SONARR));
else if(url.includes("Discovery/Request")&&method==="POST")resolve({Success:true,Message:"Request submitted to Jellyseerr."});
else if(url.includes("Discovery")&&!url.includes("Services")&&!url.includes("Request"))resolve(structuredClone(MOCK_DISCOVERY));
else if(url.includes("Seerr/Test"))resolve({success:true,message:"Connected to Jellyseerr (demo)"});
else if(url.includes("ArrIntegration/TestConnection"))resolve({Success:true,Message:"Connection successful (demo)"});
else if(url.includes("ArrIntegration/Compare/"))resolve(structuredClone(MOCK_ARR_COMPARE));
else if(url.includes("Logs/Download")){var lt=MOCK_LOGS.Entries.map(function(e){return e.Timestamp+" ["+e.Level+"] "+e.Source+": "+e.Message;}).join("\n");resolve(lt);}
else if(url.includes("Logs")&&method==="DELETE"){MOCK_LOGS.Entries=[];MOCK_LOGS.TotalCount=0;resolve({});}
else if(url.includes("Logs")){var lm=url.match(/[?&]limit=(\d+)/);var ln=lm?parseInt(lm[1],10):MOCK_LOGS.Entries.length;resolve({Entries:structuredClone(MOCK_LOGS.Entries.slice(0,ln)),TotalCount:MOCK_LOGS.TotalCount});}
else if(url.includes("Recommendations/Diagnostics/Ensemble")){var ep=url.split("userId=");var eid=ep.length>1?decodeURIComponent(ep[1].split("&")[0]):"";resolve(structuredClone(MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[eid]||MOCK_ENSEMBLE_DIAGNOSTICS));}
else if(url.includes("Recommendations/WatchProfile/")){var wp=url.split("WatchProfile/");var wid=wp.length>1?wp[1].split("?")[0]:"";resolve(structuredClone(MOCK_WATCH_PROFILES[wid]||{}));}
else if(url.includes("Recommendations/WatchProfiles"))resolve(structuredClone(MOCK_WATCH_PROFILES));
else if(url.includes("Recommendations/")&&!url.includes("WatchProfile")){var rp=url.split("Recommendations/");var rid=rp.length>1?rp[1].split("?")[0]:"";var ur=MOCK_RECOMMENDATIONS.filter(function(r){return r.UserId===rid;});resolve(structuredClone(ur.length>0?ur[0]:{}));}
else if(url.includes("Recommendations"))resolve(structuredClone(MOCK_RECOMMENDATIONS));
else if(url.includes("UserActivity/User/")){var ua=url.split("UserActivity/User/");var uid=ua.length>1?ua[1].split("?")[0]:"";resolve(structuredClone(MOCK_USER_ACTIVITY[uid]||[]));}
else if(url.includes("UserActivity/Latest"))resolve(structuredClone(MOCK_USER_ACTIVITY));
else if(url.includes("LibraryInsights"))resolve(structuredClone(MOCK_LIBRARY_INSIGHTS));
else if(url.includes("Libraries"))resolve(MOCK_LIBRARIES);
else{console.warn("Mock: unhandled",url);resolve({});}
},150+Math.random()*200);});}
};

var _of=window.fetch.bind(window);
window.fetch=function(url,opts){if(typeof url==="string"&&url.startsWith("mock://")){return ApiClient.ajax({url:url,type:(opts?.method)||"GET",data:opts?.body,dataType:"json"}).then(function(d){var b=typeof d==="string"?d:JSON.stringify(d);var ct=url.includes("Logs/Download")?"text/plain; charset=utf-8":"application/json";return new Response(b,{status:200,headers:{"Content-Type":ct}});});}return _of(url,opts);};
