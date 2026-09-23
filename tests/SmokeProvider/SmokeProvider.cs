// TEST FIXTURE ONLY. Never installed or included in the distributable plugin ZIP.
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

public sealed class SmokePlugin : BasePlugin<BasePluginConfiguration>
{
    public SmokePlugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml) { }
    public override string Name => "French Originals TEST provider";
    public override Guid Id => Guid.Parse("edb4b3f5-c6d6-4c46-9ba3-e71034a6b7a7");
}
public abstract class SmokeProvider<T,TInfo> : IRemoteMetadataProvider<T,TInfo>
    where T:BaseItem,IHasLookupInfo<TInfo>,new() where TInfo:ItemLookupInfo,new()
{
    public string Name => "Smoke French";
    public Task<HttpResponseMessage> GetImageResponse(string url,CancellationToken token)=>throw new NotSupportedException();
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo info,CancellationToken token)=>Task.FromResult<IEnumerable<RemoteSearchResult>>([]);
    public Task<MetadataResult<T>> GetMetadata(TInfo info,CancellationToken token)
    {
        var id=info.ProviderIds.GetValueOrDefault("Tmdb")??"900000001";
        var original=id=="900000002"?"en":"fr";
        var french=info.MetadataLanguage=="fr";
        return Task.FromResult(new MetadataResult<T>
        {
            HasMetadata=true,ResultLanguage=info.MetadataLanguage,QueriedById=true,
            Item=new T { Name=french?"Titre français de test":"English test title",Overview=french?"Résumé français de test.":"English test overview.",
                OriginalLanguage=original,ProviderIds=new(StringComparer.OrdinalIgnoreCase){{"Tmdb",id}} }
        });
    }
}
public sealed class MovieProvider:SmokeProvider<Movie,MovieInfo>;
public sealed class SeriesProvider:SmokeProvider<Series,SeriesInfo>;
public sealed class SeasonProvider:SmokeProvider<Season,SeasonInfo>;
public sealed class EpisodeProvider:SmokeProvider<Episode,EpisodeInfo>;
