// Mock data for demo. ~9.2TB total.
var MOCK_TRANSLATIONS = null;
function _lib(n,t,o){var s=(o.VideoSize||0)+(o.AudioSize||0)+(o.SubtitleSize||0)+(o.ImageSize||0)+(o.NfoSize||0)+(o.TrickplaySize||0)+(o.BookSize||0)+(o.OtherSize||0);return {LibraryName:n,CollectionType:t,RootPaths:o.RootPaths||[],VideoSize:0,AudioSize:0,SubtitleSize:0,ImageSize:0,NfoSize:0,TrickplaySize:0,BookSize:0,OtherSize:0,VideoFileCount:0,AudioFileCount:0,SubtitleFileCount:0,ImageFileCount:0,NfoFileCount:0,TrickplayFileCount:0,TrickplayFolderCount:o.TrickplayFolderCount||0,BookFileCount:0,OtherFileCount:0,TotalSize:s,VideoCodecs:{},VideoAudioCodecs:{},MusicAudioCodecs:{},BookFormats:{},BookFormatSizes:{},ContainerFormats:{},Resolutions:{},VideoCodecPaths:{},VideoAudioCodecPaths:{},MusicAudioCodecPaths:{},BookFormatPaths:{},ContainerFormatPaths:{},ResolutionPaths:{},VideoCodecSizes:{},VideoAudioCodecSizes:{},MusicAudioCodecSizes:{},ContainerSizes:{},ResolutionSizes:{},VideosWithoutSubtitles:0,VideosWithoutImages:0,VideosWithoutNfo:0,OrphanedMetadataDirectories:0,VideosWithoutSubtitlesPaths:[],VideosWithoutImagesPaths:[],VideosWithoutNfoPaths:[],OrphanedMetadataDirectoriesPaths:[],...o};}

var _moviesLib=_lib("Movies","movies",{
RootPaths:["/data/movies"],
VideoSize:5283942948864,SubtitleSize:2684354560,ImageSize:3758096384,NfoSize:429496729,TrickplaySize:16106127360,OtherSize:214748160,
VideoFileCount:1247,SubtitleFileCount:2840,ImageFileCount:5200,NfoFileCount:1247,TrickplayFileCount:1100,TrickplayFolderCount:1100,OtherFileCount:38,
VideoCodecs:{"HEVC":580,"H.264":520,"AV1":95,"MPEG-2":32,"VP9":20},
VideoAudioCodecs:{"AAC":510,"AC3":320,"EAC3":180,"DTS":140,"TrueHD":72,"Opus":25},
ContainerFormats:{"MKV":780,"MP4":410,"AVI":42,"WebM":15},
Resolutions:{"2160p":380,"1080p":650,"720p":150,"480p":52,"576p":15},
VideoCodecSizes:{"HEVC":2642000000000,"H.264":1847000000000,"AV1":537000000000,"MPEG-2":172000000000,"VP9":85942948864},
VideoAudioCodecSizes:{"AAC":2100000000000,"AC3":1400000000000,"EAC3":800000000000,"DTS":600000000000,"TrueHD":350000000000,"Opus":33942948864},
ContainerSizes:{"MKV":3500000000000,"MP4":1550000000000,"AVI":150000000000,"WebM":83942948864},
ResolutionSizes:{"2160p":3200000000000,"1080p":1700000000000,"720p":280000000000,"480p":80000000000,"576p":23942948864},
VideoCodecPaths:{"HEVC":["/data/movies/Inception (2010)/Inception.mkv","/data/movies/Interstellar (2014)/Interstellar.mkv"],"H.264":["/data/movies/The Matrix (1999)/The Matrix.mp4"],"AV1":["/data/movies/Oppenheimer (2023)/Oppenheimer.mkv"],"MPEG-2":["/data/movies/Old Movie (1992)/Old Movie.avi"],"VP9":["/data/movies/Big Buck Bunny (2008)/Big Buck Bunny.webm"]},
VideoAudioCodecPaths:{"AAC":["/data/movies/The Matrix (1999)/The Matrix.mp4"],"AC3":["/data/movies/Inception (2010)/Inception.mkv"],"EAC3":["/data/movies/Dune Part Two (2024)/Dune Part Two.mkv"],"DTS":["/data/movies/Interstellar (2014)/Interstellar.mkv"],"TrueHD":["/data/movies/Oppenheimer (2023)/Oppenheimer.mkv"],"Opus":["/data/movies/Big Buck Bunny (2008)/Big Buck Bunny.webm"]},
ContainerFormatPaths:{"MKV":["/data/movies/Inception (2010)/Inception.mkv"],"MP4":["/data/movies/The Matrix (1999)/The Matrix.mp4"],"AVI":["/data/movies/Old Movie (1992)/Old Movie.avi"],"WebM":["/data/movies/Big Buck Bunny (2008)/Big Buck Bunny.webm"]},
ResolutionPaths:{"2160p":["/data/movies/Dune Part Two (2024)/Dune Part Two.mkv"],"1080p":["/data/movies/The Matrix (1999)/The Matrix.mp4"],"720p":["/data/movies/Pulp Fiction (1994)/Pulp Fiction.mkv"],"480p":["/data/movies/Old Movie (1992)/Old Movie.avi"],"576p":["/data/movies/French Film (1998)/French Film.mkv"]},
DynamicRanges:{"SDR":780,"HDR10":280,"Dolby Vision":130,"HLG":37,"HDR10+":20},
DynamicRangeSizes:{"SDR":2000000000000,"HDR10":1800000000000,"Dolby Vision":1000000000000,"HLG":300000000000,"HDR10+":183942948864},
DynamicRangePaths:{"HDR10":["/data/movies/Inception (2010)/Inception.mkv"],"Dolby Vision":["/data/movies/Dune Part Two (2024)/Dune Part Two.mkv"],"HDR10+":["/data/movies/Oppenheimer (2023)/Oppenheimer.mkv"]},
VideosWithoutSubtitles:8,VideosWithoutImages:2,VideosWithoutNfo:1,
VideosWithoutSubtitlesPaths:["/data/movies/Home Video (2023)/Home Video.mp4","/data/movies/Old Movie (1992)/Old Movie.avi"],
VideosWithoutImagesPaths:["/data/movies/Home Video (2023)/Home Video.mp4"],
VideosWithoutNfoPaths:["/data/movies/Home Video (2023)/Home Video.mp4"]
});

var _tvLib=_lib("TV Shows","tvshows",{
RootPaths:["/data/tv"],
VideoSize:3435973836800,SubtitleSize:3221225472,ImageSize:4294967296,NfoSize:644245094,TrickplaySize:21474836480,OtherSize:322122547,
VideoFileCount:3842,SubtitleFileCount:7200,ImageFileCount:9500,NfoFileCount:3842,TrickplayFileCount:3200,TrickplayFolderCount:3200,OtherFileCount:52,
VideoCodecs:{"HEVC":1800,"H.264":1650,"AV1":200,"VP9":120,"MPEG-2":72},
VideoAudioCodecs:{"AAC":1900,"AC3":850,"EAC3":520,"DTS":280,"Opus":192,"TrueHD":100},
ContainerFormats:{"MKV":2400,"MP4":1200,"AVI":140,"WebM":72,"TS":30},
Resolutions:{"2160p":520,"1080p":2400,"720p":680,"480p":180,"576p":62},
VideoCodecSizes:{"HEVC":1700000000000,"H.264":1300000000000,"AV1":250000000000,"VP9":120000000000,"MPEG-2":65973836800},
VideoAudioCodecSizes:{"AAC":1600000000000,"AC3":800000000000,"EAC3":500000000000,"DTS":300000000000,"Opus":150000000000,"TrueHD":85973836800},
ContainerSizes:{"MKV":2200000000000,"MP4":1000000000000,"AVI":150000000000,"WebM":55000000000,"TS":30973836800},
ResolutionSizes:{"2160p":1100000000000,"1080p":1800000000000,"720p":400000000000,"480p":100000000000,"576p":35973836800},
VideoCodecPaths:{"HEVC":["/data/tv/Breaking Bad/Season 01/S01E01.mkv"],"H.264":["/data/tv/Friends/Season 01/S01E01.mp4"],"AV1":["/data/tv/Planet Earth III/Season 01/S01E01.mkv"],"VP9":["/data/tv/YouTube Originals/S01E01.webm"],"MPEG-2":["/data/tv/Old Show (1995)/S01E01.ts"]},
VideoAudioCodecPaths:{"AAC":["/data/tv/Friends/Season 01/S01E01.mp4"],"AC3":["/data/tv/Breaking Bad/Season 01/S01E01.mkv"],"EAC3":["/data/tv/The Expanse/Season 01/S01E01.mkv"],"DTS":["/data/tv/Planet Earth III/Season 01/S01E01.mkv"],"Opus":["/data/tv/YouTube Originals/S01E01.webm"],"TrueHD":["/data/tv/Breaking Bad/Season 05/S05E16.mkv"]},
ContainerFormatPaths:{"MKV":["/data/tv/Breaking Bad/Season 01/S01E01.mkv"],"MP4":["/data/tv/Friends/Season 01/S01E01.mp4"],"AVI":["/data/tv/Old Show (1995)/S01E01.avi"],"WebM":["/data/tv/YouTube Originals/S01E01.webm"],"TS":["/data/tv/Old Show (1995)/S01E01.ts"]},
ResolutionPaths:{"2160p":["/data/tv/Planet Earth III/Season 01/S01E01.mkv"],"1080p":["/data/tv/Breaking Bad/Season 01/S01E01.mkv"],"720p":["/data/tv/The Office/Season 01/S01E01.mp4"],"480p":["/data/tv/Friends/Season 01/S01E01.mp4"],"576p":["/data/tv/Old Show (1995)/S01E01.avi"]},
DynamicRanges:{"SDR":3100,"HDR10":450,"Dolby Vision":200,"HLG":62,"HDR10+":30},
DynamicRangeSizes:{"SDR":2200000000000,"HDR10":650000000000,"Dolby Vision":380000000000,"HLG":130000000000,"HDR10+":75973836800},
DynamicRangePaths:{"HDR10":["/data/tv/Planet Earth III/Season 01/S01E01.mkv"],"Dolby Vision":["/data/tv/The Expanse/Season 01/S01E01.mkv"]},
VideosWithoutSubtitles:45,VideosWithoutImages:8,OrphanedMetadataDirectories:2,
VideosWithoutSubtitlesPaths:["/data/tv/New Show/Season 01/S01E01.mkv","/data/tv/New Show/Season 01/S01E02.mkv"],
VideosWithoutImagesPaths:["/data/tv/New Show/Season 01/S01E01.mkv"],
OrphanedMetadataDirectoriesPaths:["/data/tv/Cancelled Show/","/data/tv/Removed Series/"]
});

var _musicLib=_lib("Music","music",{
RootPaths:["/data/music"],AudioSize:214748364800,ImageSize:1073741824,
AudioFileCount:12450,ImageFileCount:2000,
MusicAudioCodecs:{"FLAC":6000,"MP3":3200,"Opus":1500,"AAC":1000,"ALAC":500,"WavPack":250},
MusicAudioCodecSizes:{"FLAC":140000000000,"MP3":32000000000,"Opus":18000000000,"AAC":12000000000,"ALAC":10000000000,"WavPack":2748364800},
MusicAudioCodecPaths:{"FLAC":["/data/music/Pink Floyd/01.flac"],"MP3":["/data/music/Various/song.mp3"]}
});

var _booksLib=_lib("Books","books",{
RootPaths:["/data/books"],BookSize:48318382080,ImageSize:268435456,
BookFileCount:3120,ImageFileCount:850,
BookFormats:{"EPUB":1840,"PDF":720,"CBZ":360,"MOBI":150,"AZW3":50},
BookFormatSizes:{"EPUB":18253611008,"PDF":22548578304,"CBZ":5905580032,"MOBI":1181116006,"AZW3":429496730},
BookFormatPaths:{"EPUB":["/data/books/Brandon Sanderson/Mistborn.epub"],"PDF":["/data/books/OReilly/Designing Data-Intensive Applications.pdf"],"CBZ":["/data/books/Comics/Saga Vol 1.cbz"]}
});

var MOCK_STATISTICS={
ScanTimestamp:new Date(Date.now()-300000).toISOString(),
Libraries:[_moviesLib,_tvLib,_musicLib,_booksLib],
Movies:[_moviesLib],TvShows:[_tvLib],Music:[_musicLib],Books:[_booksLib],Other:[],
TotalMovieVideoSize:_moviesLib.VideoSize,
TotalTvShowVideoSize:_tvLib.VideoSize,
TotalMusicAudioSize:_musicLib.AudioSize,
TotalBookSize:_booksLib.BookSize,
TotalBookFileCount:_booksLib.BookFileCount,
TotalBookFormats:_booksLib.BookFormats,
TotalTrickplaySize:_moviesLib.TrickplaySize+_tvLib.TrickplaySize,
TotalSubtitleSize:_moviesLib.SubtitleSize+_tvLib.SubtitleSize,
TotalImageSize:_moviesLib.ImageSize+_tvLib.ImageSize+_musicLib.ImageSize+_booksLib.ImageSize,
TotalNfoSize:_moviesLib.NfoSize+_tvLib.NfoSize,
TotalVideoFileCount:_moviesLib.VideoFileCount+_tvLib.VideoFileCount,
TotalAudioFileCount:_musicLib.AudioFileCount,
MovieRootPaths:["/data/movies"],TvShowRootPaths:["/data/tv"],MusicRootPaths:["/data/music"],OtherRootPaths:[]
};

var MOCK_CONFIG={
ExcludedLibraries:"",OrphanMinAgeDays:0,
TrickplayTaskMode:"DryRun",EmptyMediaFolderTaskMode:"Activate",
OrphanedSubtitleTaskMode:"DryRun",LinkRepairTaskMode:"Deactivate",
UseTrash:true,TrashFolderPath:".jellyfin-trash",TrashRetentionDays:30,
Language:"en",PluginLogLevel:"INFO",
RadarrInstances:[{Name:"Radarr Main",Url:"http://192.168.1.100:7878",ApiKey:"demo-key-1234"},{Name:"Radarr 4K",Url:"http://192.168.1.100:7879",ApiKey:"demo-key-5678"}],
SonarrInstances:[{Name:"Sonarr",Url:"http://192.168.1.100:8989",ApiKey:"demo-key-abcd"}],
SeerrUrl:"http://192.168.1.100:5055",SeerrApiKey:"demo-seerr-key",
SeerrCleanupTaskMode:"DryRun",SeerrCleanupAgeDays:365,
RecommendationsTaskMode:"Activate",DiscoveryUserAccessEnabled:true,ConfigVersion:3
};

var MOCK_CLEANUP_STATS={TotalBytesFreed:8589934592,TotalItemsDeleted:247,LastCleanupTimestamp:new Date(Date.now()-86400000).toISOString()};

var MOCK_HISTORY=(function(){var s=[],now=Date.now();for(var i=30;i>=0;i--){var d=new Date(now-i*86400000),g=(30-i)*50000000000;s.push({Timestamp:d.toISOString(),TotalVideoSize:8500000000000+g,TotalAudioSize:214748364800+(30-i)*500000000,TotalSubtitleSize:5905580032,TotalImageSize:9126805504,TotalNfoSize:1073741823,TotalTrickplaySize:37580963840+(30-i)*70000000,TotalOtherSize:536870707,TotalVideoFileCount:5089+(30-i)*3,TotalAudioFileCount:12450+(30-i)*8,TotalSize:8769971724706+g+(30-i)*570000000,LibraryCount:3});}return s;})();

var MOCK_TRASH_CONTENTS={RetentionDays:30,Libraries:[
{LibraryName:"Movies",Items:[{OriginalName:"Old Movie (1995)",Size:4294967296,TrashedDate:new Date(Date.now()-172800000).toISOString(),PurgeDate:new Date(Date.now()+2419200000).toISOString(),IsDirectory:true},{OriginalName:"Duplicate.mkv",Size:2147483648,TrashedDate:new Date(Date.now()-604800000).toISOString(),PurgeDate:new Date(Date.now()+1987200000).toISOString(),IsDirectory:false}]},
{LibraryName:"TV Shows",Items:[{OriginalName:"Cancelled Show",Size:10737418240,TrashedDate:new Date(Date.now()-86400000).toISOString(),PurgeDate:new Date(Date.now()+2505600000).toISOString(),IsDirectory:true}]}
]};
var MOCK_TRASH_FOLDERS={Paths:["/data/movies/.jellyfin-trash","/data/tv/.jellyfin-trash"]};
var MOCK_TRASH_FOLDERS_FOR_PATH=function(trashPath){
    // Simulate: relative paths resolve per library, absolute paths check directly
    if(!trashPath)return{Paths:[],IsAbsolute:false};
    var isAbs=/^(\/|[A-Za-z]:[\\/]|\\\\)/.test(trashPath);
    if(isAbs)return{Paths:[trashPath],IsAbsolute:true};
    // Relative: resolve against each library root
    var libs=["/data/movies","/data/tv"];
    var paths=libs.map(function(l){return l+"/"+trashPath;});
    return{Paths:paths,IsAbsolute:false};
};

var MOCK_ARR_COMPARE={
InBoth:["Inception (2010)","Interstellar (2014)","The Dark Knight (2008)","Dune Part Two (2024)","Oppenheimer (2023)"],
InArrOnly:["Arrival (2016)"],
InArrOnlyMissing:["Tenet (2020)","Blade Runner 2049 (2017)"],
InJellyfinOnly:["Home Video (2023)","Family Recording (2024)"]
};

var MOCK_LOGS={Entries:(function(){var e=[],src=["MediaStatistics","TrickplayCleaner","EmptyFolderCleaner","SubtitleCleaner","LinkRepair","TrashService","ArrIntegration"],lvl=["INFO","INFO","INFO","DEBUG","WARN","ERROR","INFO","INFO","DEBUG","INFO"],msg=["Statistics scan started for 3 libraries","Scanning: Movies (1247 video files)","Scanning: TV Shows (3842 video files)","Processing: /data/movies/Inception (2010)","Trickplay folder has no matching video: /data/trickplay/deleted-movie","Failed to access: /data/restricted - Access denied","Scan completed in 14.2s - 5089 videos, 12450 audio files","Empty folder cleanup: 3 folders deleted, 45.2 MB freed","Checking subtitle: /data/movies/Old Movie/Old Movie.en.srt","Link repair: 2 files repaired, 0 errors"],now=Date.now();for(var i=0;i<msg.length;i++){e.push({Timestamp:new Date(now-(msg.length-i)*60000).toISOString(),Level:lvl[i],Source:src[i%src.length],Message:msg[i],Exception:lvl[i]==="ERROR"?"System.UnauthorizedAccessException: Access denied.\n   at System.IO.Directory.InternalEnumerateEntries(...)":null});}return e;})(),TotalCount:10};

var MOCK_GROWTH_TIMELINE=(function(){
var dp=[],start=new Date(2016,5,1),now=new Date(),cs=0,cf=0,m=new Date(start),seed=42;
function rnd(){seed=(seed*16807)%2147483647;return(seed-1)/2147483646;}
while(m<=now){var y=m.getFullYear(),mo=m.getMonth(),yi=y-2016;
var bf=Math.floor(10+yi*8+rnd()*(15+yi*5));
if(mo===0||mo===9||mo===11)bf=Math.floor(bf*(1.3+rnd()*0.4));
var avg=(1.5+yi*0.5+rnd()*1.5)*1073741824;
cs+=Math.floor(bf*avg);cf+=bf;
dp.push({date:new Date(y,mo,1).toISOString(),cumulativeSize:cs,cumulativeFileCount:cf});
m.setMonth(m.getMonth()+1);}
return{granularity:"monthly",earliestFileDate:start.toISOString(),computedAt:now.toISOString(),totalDirectoriesScanned:cf,dataPoints:dp};})();

var MOCK_LIBRARY_INSIGHTS={
Largest:[
{Name:"Breaking Bad",Size:214748364800,CreatedUtc:"2018-09-01T08:00:00Z",ModifiedUtc:"2024-03-05T12:00:00Z",LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:""},
{Name:"Planet Earth III",Size:161061273600,CreatedUtc:"2023-10-01T10:00:00Z",ModifiedUtc:"2024-01-20T14:00:00Z",LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:""},
{Name:"Game of Thrones",Size:150323855360,CreatedUtc:"2017-04-14T08:00:00Z",ModifiedUtc:"2023-08-10T09:00:00Z",LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:""},
{Name:"The Expanse",Size:128849018880,CreatedUtc:"2019-12-13T11:00:00Z",ModifiedUtc:"2024-02-01T07:00:00Z",LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:""},
{Name:"Interstellar (2014)",Size:85899345920,CreatedUtc:"2020-03-15T10:00:00Z",ModifiedUtc:"2024-01-10T08:30:00Z",LibraryName:"Movies",CollectionType:"movies",ChangeType:""},
{Name:"Dune Part Two (2024)",Size:75161927680,CreatedUtc:"2024-03-01T12:00:00Z",ModifiedUtc:"2024-03-01T12:00:00Z",LibraryName:"Movies",CollectionType:"movies",ChangeType:""},
{Name:"Oppenheimer (2023)",Size:64424509440,CreatedUtc:"2023-07-21T16:00:00Z",ModifiedUtc:"2024-02-28T11:00:00Z",LibraryName:"Movies",CollectionType:"movies",ChangeType:""},
{Name:"Inception (2010)",Size:53687091200,CreatedUtc:"2018-01-05T09:00:00Z",ModifiedUtc:"2023-06-10T10:00:00Z",LibraryName:"Movies",CollectionType:"movies",ChangeType:""}
],
LargestTotalSize:1084905390080,
Recent:[
{Name:"Dune Part Two (2024)",Size:75161927680,CreatedUtc:new Date(Date.now()-3*86400000).toISOString(),ModifiedUtc:new Date(Date.now()-3*86400000).toISOString(),LibraryName:"Movies",CollectionType:"movies",ChangeType:"added"},
{Name:"The Penguin",Size:42949672960,CreatedUtc:new Date(Date.now()-5*86400000).toISOString(),ModifiedUtc:new Date(Date.now()-2*86400000).toISOString(),LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:"added"},
{Name:"Oppenheimer (2023)",Size:64424509440,CreatedUtc:"2023-07-21T16:00:00Z",ModifiedUtc:new Date(Date.now()-7*86400000).toISOString(),LibraryName:"Movies",CollectionType:"movies",ChangeType:"changed"},
{Name:"Breaking Bad",Size:214748364800,CreatedUtc:"2018-09-01T08:00:00Z",ModifiedUtc:new Date(Date.now()-10*86400000).toISOString(),LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:"changed"},
{Name:"Civil War (2024)",Size:32212254720,CreatedUtc:new Date(Date.now()-12*86400000).toISOString(),ModifiedUtc:new Date(Date.now()-12*86400000).toISOString(),LibraryName:"Movies",CollectionType:"movies",ChangeType:"added"},
{Name:"Shogun",Size:107374182400,CreatedUtc:new Date(Date.now()-15*86400000).toISOString(),ModifiedUtc:new Date(Date.now()-8*86400000).toISOString(),LibraryName:"TV Shows",CollectionType:"tvshows",ChangeType:"added"}
],
RecentTotalCount:6,
LibrarySizes:{"Movies":_moviesLib.TotalSize,"TV Shows":_tvLib.TotalSize},
ComputedAtUtc:new Date().toISOString()
};

var MOCK_LIBRARIES=[{name:"Movies",collectionType:"movies"},{name:"TV Shows",collectionType:"tvshows"},{name:"Music",collectionType:"music"}];

var MOCK_LIBRARY_PATHS={libraryPaths:[
{name:"Movies",path:"/data/movies"},
{name:"TV Shows",path:"/data/tv"},
{name:"Music",path:"/data/music"}
]};

var MOCK_BROWSE_FOLDERS=(function(){
// Simulated filesystem tree for the folder browser preview
    var tree={
        "/":["data","home","mnt","opt","var"],
        "/data":["movies","tv","music","backups","downloads"],
        "/data/movies":[".jellyfin-trash","Action","Comedy","Sci-Fi"],
        "/data/movies/.jellyfin-trash":[],
        "/data/movies/Action":[],
        "/data/movies/Comedy":[],
        "/data/movies/Sci-Fi":[],
        "/data/tv":[".jellyfin-trash","Drama","Animation"],
        "/data/tv/.jellyfin-trash":[],
        "/data/tv/Drama":[],
        "/data/tv/Animation":[],
        "/data/music":["FLAC","MP3","Podcasts"],
        "/data/music/FLAC":[],
        "/data/music/MP3":[],
        "/data/music/Podcasts":[],
        "/data/backups":[],
        "/data/downloads":["incomplete","complete"],
        "/data/downloads/incomplete":[],
        "/data/downloads/complete":[],
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
{TmdbId:746036,MediaType:"movie",Title:"The Fall Guy",Year:2024,Score:0.88,Reason:"Trending \u2014 new and highly rated",ReasonKey:"reasonTrending",Genres:["Action","Comedy"],TmdbRating:7.3,PosterPath:"/tSz1qsmSJon0rqjHBxXZmrotuse.jpg",Overview:"A down-and-out stuntman must track down a missing movie star, solve a conspiracy, and try to win back the love of his life while still doing his day job.",AlreadyRequested:false},
{TmdbId:108545,MediaType:"tv",Title:"3 Body Problem",Year:2024,Score:0.86,Reason:"Because you like Sci-Fi",ReasonKey:"reasonGenre",RelatedInfo:"Sci-Fi",Genres:["Sci-Fi","Drama","Mystery"],TmdbRating:7.6,PosterPath:"/sg4xJGSJKjGZ3YKDDmzqY9cHCOB.jpg",Overview:"A fateful decision made in 1960s China reverberates across space and time to a group of brilliant scientists in the present day.",AlreadyRequested:false},
{TmdbId:786892,MediaType:"movie",Title:"Furiosa: A Mad Max Saga",Year:2024,Score:0.84,Reason:"With actors you like",ReasonKey:"reasonPeople",Genres:["Action","Sci-Fi","Adventure"],TmdbRating:7.5,PosterPath:"/iADOJ8Zymht2JPMoy3R7xceZprc.jpg",Overview:"As the world fell, young Furiosa is snatched from the Green Place of Many Mothers and falls into the hands of a great Biker Horde led by the Warlord Dementus.",AlreadyRequested:false},
{TmdbId:126308,MediaType:"tv",Title:"Sh\u014dgun",Year:2024,Score:0.81,Reason:"Highly rated",ReasonKey:"reasonHighlyRated",Genres:["Drama","War"],TmdbRating:8.7,PosterPath:"/7O4iVfOMQmdCSxhOg1WnzG1AgYT.jpg",Overview:"In Japan in the year 1600, at the dawn of a century-defining civil war, Lord Yoshii Toranaga is fighting for his life as his enemies on the Council of Regents unite against him.",AlreadyRequested:false},
{TmdbId:1184918,MediaType:"movie",Title:"The Wild Robot",Year:2024,Score:0.78,Reason:"New and highly rated",ReasonKey:"reasonTrending",Genres:["Animation","Sci-Fi","Family"],TmdbRating:8.4,PosterPath:"/wTnV3PCVW5O92JMrFvvrRcV39RU.jpg",Overview:"After a shipwreck, an intelligent robot called Roz is stranded on an uninhabited island. To survive the harsh environment, Roz bonds with the island's animals.",AlreadyRequested:false}
]},
{UserId:_uid2,UserName:"Bob",GeneratedAt:new Date(Date.now()-3600000).toISOString(),Recommendations:[
{TmdbId:840430,MediaType:"movie",Title:"The Holdovers",Year:2023,Score:0.90,Reason:"From a director you like",ReasonKey:"reasonStudioNamed",RelatedInfo:"Alexander Payne",Genres:["Comedy","Drama"],TmdbRating:7.9,PosterPath:"/VHSzNBTwxV8vh7wylo7O9CLdac.jpg",Overview:"A curmudgeonly instructor at a New England prep school is forced to remain on campus during Christmas break to babysit a handful of students with nowhere to go.",AlreadyRequested:false},
{TmdbId:203737,MediaType:"tv",Title:"Ripley",Year:2024,Score:0.87,Reason:"Because you watched {0}",ReasonKey:"reasonGenreAndCollab",RelatedInfo:"Drama",Genres:["Crime","Drama","Thriller"],TmdbRating:7.8,PosterPath:"/bkZpMfsMwfqRVEwfUn8i3qi2NDE.jpg",Overview:"Tom Ripley, a grifter in early 1960s New York, is hired by a wealthy man to travel to Italy to try to convince his vagabond son to return home.",AlreadyRequested:false},
{TmdbId:792307,MediaType:"movie",Title:"Poor Things",Year:2023,Score:0.85,Reason:"Popular among similar viewers",ReasonKey:"reasonCollaborative",Genres:["Comedy","Drama","Sci-Fi"],TmdbRating:7.8,PosterPath:"/kCGlIMHnOm8JPXq3rXM6c5wMxcT.jpg",Overview:"Brought back to life by an unorthodox scientist, a young woman runs off with a debauched lawyer on a whirlwind adventure across the continents.",AlreadyRequested:false},
{TmdbId:225545,MediaType:"tv",Title:"A Gentleman in Moscow",Year:2024,Score:0.82,Reason:"Matches your top genres",ReasonKey:"reasonGenre",RelatedInfo:"Drama",Genres:["Drama","History"],TmdbRating:7.6,PosterPath:"/bKf6CCAagFJiGOddGb4u0yf5kMV.jpg",Overview:"A Count is sentenced to house arrest in a grand Moscow hotel for decades. As Russia undergoes decades of tumultuous change, he must find purpose in an ever-shrinking world.",AlreadyRequested:false},
{TmdbId:467244,MediaType:"movie",Title:"The Zone of Interest",Year:2023,Score:0.79,Reason:"Highly rated drama",ReasonKey:"reasonHighlyRated",Genres:["Drama","History","War"],TmdbRating:7.1,PosterPath:"/hUu9zyZmDd8VZegKi1iK1Vk0RYS.jpg",Overview:"The commandant of Auschwitz, Rudolf H\u00f6ss, and his wife Hedwig, strive to build a dream life for their family in a house and garden next to the camp.",AlreadyRequested:false}
]}
];

// Radarr services: 2 quality profiles (triggers popup for movie requests)
var MOCK_SEERR_SERVICES_RADARR=[{
id:1,name:"Radarr Main",isDefault:true,is4k:false,
activeProfileId:4,activeDirectory:"/data/movies",
profiles:[{id:4,name:"HD-1080p"},{id:6,name:"Ultra-HD"}],
rootFolders:[{id:1,path:"/data/movies"}]
}];

// Sonarr services: empty (no popup for TV requests - uses server defaults)
var MOCK_SEERR_SERVICES_SONARR=[];

var MOCK_RECOMMENDATIONS=[
{UserId:_uid1,UserName:"Alice",Recommendations:[
{Name:"Blade Runner 2049",Score:0.94,ItemType:"Movie",Genres:["Sci-Fi","Thriller"],Year:2017,Reason:"Because you watched {0}",ReasonKey:"recsBecauseWatched",RelatedItemName:"Blade Runner"},
{Name:"Ex Machina",Score:0.91,ItemType:"Movie",Genres:["Sci-Fi","Thriller"],Year:2014,Reason:"Matches your top genres",ReasonKey:"recsGenreMatch"},
{Name:"The Martian",Score:0.87,ItemType:"Movie",Genres:["Sci-Fi","Adventure"],Year:2015,Reason:"Directed by Ridley Scott",ReasonKey:"recsDirectorMatch"},
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
// Alice has crossed the per-user threshold, so she gets her own individually trained model with her own
// blend and maturity. Bob is still below it, so he falls back to the shared global strategy (no model yet).
// Switching the selected user in the demo swaps between these two footnote states.
var MOCK_ENSEMBLE_DIAGNOSTICS={Available:true,IsPerUser:false,Alpha:0.62,NeuralBeta:0.28,QualityGateFrozen:false,SigmoidMidpointOffset:-6,EffectiveSigmoidMidpoint:44,Trend:"Improving",TrainingExampleCount:342,MetricsHistoryCount:10,AlphaMin:0.15,AlphaMax:0.85,NeuralEnabled:true};
var MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER={};
MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[_uid1]={Available:true,IsPerUser:true,UserName:"Alice",Alpha:0.68,NeuralBeta:0.31,QualityGateFrozen:false,SigmoidMidpointOffset:-6,EffectiveSigmoidMidpoint:44,Trend:"Improving",TrainingExampleCount:412,MetricsHistoryCount:10,AlphaMin:0.15,AlphaMax:0.85,NeuralEnabled:true};
// Bob has no per-user model yet (IsPerUser:false), so his footnote shows the shared global snapshot with only
// his name attached. Deriving it from the global object keeps the two in sync instead of drifting apart.
MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[_uid2]=Object.assign({},MOCK_ENSEMBLE_DIAGNOSTICS,{UserName:"Bob"});
MOCK_WATCH_PROFILES[_uid1]={WatchedMovieCount:85,WatchedEpisodeCount:240,WatchedSeriesCount:12,FavoriteCount:15,GenreDistribution:{"Sci-Fi":45,"Action":38,"Thriller":22,"Drama":18,"Adventure":12,"Mystery":8,"Horror":5}};
MOCK_WATCH_PROFILES[_uid2]={WatchedMovieCount:42,WatchedEpisodeCount:180,WatchedSeriesCount:8,FavoriteCount:8,GenreDistribution:{"Drama":40,"Comedy":35,"Romance":15,"Thriller":10,"Adventure":6,"War":3}};

var MOCK_USER_ACTIVITY={};
MOCK_USER_ACTIVITY[_uid1]=[
{ItemName:"Interstellar",ItemType:"Movie",TotalPlayCount:3,MostRecentWatch:new Date(Date.now()-2*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Dune Part Two",ItemType:"Movie",TotalPlayCount:2,MostRecentWatch:new Date(Date.now()-4*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"The Expanse",ItemType:"Series",TotalPlayCount:62,MostRecentWatch:new Date(Date.now()-5*86400000).toISOString(),AverageCompletionPercent:95},
{ItemName:"Oppenheimer",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-8*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Inception",ItemType:"Movie",TotalPlayCount:4,MostRecentWatch:new Date(Date.now()-25*86400000).toISOString(),AverageCompletionPercent:100}
];
MOCK_USER_ACTIVITY[_uid2]=[
{ItemName:"The Grand Budapest Hotel",ItemType:"Movie",TotalPlayCount:2,MostRecentWatch:new Date(Date.now()-86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Fleabag",ItemType:"Series",TotalPlayCount:12,MostRecentWatch:new Date(Date.now()-3*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Everything Everywhere All at Once",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-6*86400000).toISOString(),AverageCompletionPercent:100},
{ItemName:"Knives Out",ItemType:"Movie",TotalPlayCount:1,MostRecentWatch:new Date(Date.now()-14*86400000).toISOString(),AverageCompletionPercent:100}
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
    else if(url.includes("Trash/CheckAccess")&&method==="POST"){resolve({AllAccessible:true,Results:[{Path:"/data/movies/.jellyfin-trash",Exists:false,CanRead:true,CanWrite:true,HasFullAccess:true,ErrorMessage:null}]});}
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
else if(url.includes("Logs"))resolve(structuredClone(MOCK_LOGS));
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
