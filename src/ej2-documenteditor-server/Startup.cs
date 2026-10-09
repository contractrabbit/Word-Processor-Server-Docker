using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Syncfusion.EJ2.SpellChecker;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Microsoft.AspNetCore.ResponseCompression;

namespace EJ2DocumentEditorServer
{
    public class Startup
    {
        internal static string path;
        public Startup(IConfiguration configuration, IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
            path = Configuration["SPELLCHECK_DICTIONARY_PATH"];
            string jsonFileName = Configuration["SPELLCHECK_JSON_FILENAME"];
            int cacheCount;
            if (!int.TryParse(Configuration["SPELLCHECK_CACHE_COUNT"], out cacheCount))
            {
                cacheCount = 1;
            }
            //check the spell check dictionary path environment variable value and assign default data folder
            //if it is null.
            path = string.IsNullOrEmpty(path) ? Path.Combine(env.ContentRootPath, "Data") : Path.Combine(env.ContentRootPath, path);
            //Set the default spellcheck.json file if the json filename is empty.
            jsonFileName = string.IsNullOrEmpty(jsonFileName) ? Path.Combine(path, "spellcheck.json") : Path.Combine(path, jsonFileName) ;
            if (System.IO.File.Exists(jsonFileName))
            {
                string jsonImport = System.IO.File.ReadAllText(jsonFileName);
                List<DictionaryData> spellChecks = JsonConvert.DeserializeObject<List<DictionaryData>>(jsonImport);
                List<DictionaryData> spellDictCollection = new List<DictionaryData>();
                string personalDictPath = string.Empty;
                //construct the dictionary file path using customer provided path and dictionary name
                foreach (var spellCheck in spellChecks)
                {
                    spellDictCollection.Add(new DictionaryData(spellCheck.LanguadeID, Path.Combine(path, spellCheck.DictionaryPath), Path.Combine(path, spellCheck.AffixPath)));
                    personalDictPath = Path.Combine(path, spellCheck.PersonalDictPath);
                }
                SpellChecker.InitializeDictionaries(spellDictCollection, personalDictPath, cacheCount);
            }
        }

        public IConfiguration Configuration { get; }
        readonly string MyAllowSpecificOrigins = "MyPolicy";
        // Bounded cap for decompressed request bodies (see Configure). Not
        // unlimited: still a hard ceiling against gzip/br decompression
        // bombs, generous enough for the ~30 MB raw SFDT payloads this
        // service exports/imports.
        const long DecompressedBodySizeLimitBytes = 256_000_000;
        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddMemoryCache();
            services.AddControllers().AddNewtonsoftJson(options =>
            {
                // Use the default property (Pascal) casing
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            });

            services.AddCors(options =>
            {
                options.AddPolicy(MyAllowSpecificOrigins,
                builder =>
                {
                    builder.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
                });
            });
            services.Configure<GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
            services.AddResponseCompression();

            // Accepts gzip/br/deflate request bodies (all three are registered by
            // default). Note: RequestDecompressionMiddleware enforces the request
            // size limit (IHttpMaxRequestBodySizeFeature.MaxRequestBodySize)
            // against the DECOMPRESSED stream, not the compressed wire bytes
            // (verified against RequestDecompressionMiddleware.InvokeCore in the
            // ASP.NET Core 9 source, which wraps the decompression stream itself
            // in a SizeLimitedStream using that limit). Configure raises that
            // feature value to DecompressedBodySizeLimitBytes only for requests
            // that declare Content-Encoding, so uncompressed requests keep
            // Kestrel's unchanged default (~28.6 MiB) and a decompression bomb
            // still fails past the bounded cap.
            services.AddRequestDecompression();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            string license_key = Configuration["SYNCFUSION_LICENSE_KEY"];
            if (license_key!=null && license_key!=string.Empty)
                Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(license_key);
            app.UseDeveloperExceptionPage();
            // Raise the body-size cap only for requests that declare
            // Content-Encoding (i.e. will be decompressed below). This keeps
            // uncompressed requests on Kestrel's unchanged default limit
            // (~28.6 MiB) while admitting large legitimate SFDT payloads once
            // gzip/br-encoded, and keeps that raised cap bounded rather than
            // unlimited, so a decompression bomb still fails past
            // DecompressedBodySizeLimitBytes.
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("Content-Encoding"))
                {
                    IHttpMaxRequestBodySizeFeature sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (sizeFeature != null && !sizeFeature.IsReadOnly)
                    {
                        sizeFeature.MaxRequestBodySize = DecompressedBodySizeLimitBytes;
                    }
                }
                await next();
            });
            // As early in the pipeline as possible, before routing/controllers
            // consume the request body.
            app.UseRequestDecompression();
            app.UseCors("AllowAllOrigins");
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseRouting();
            app.UseAuthorization();
            app.UseCors(MyAllowSpecificOrigins);
            app.UseResponseCompression();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers().RequireCors("MyPolicy");
            });
        }

    }
}
