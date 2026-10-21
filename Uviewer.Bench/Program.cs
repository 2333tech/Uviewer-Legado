using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Uviewer.Services.Legado;
using Uviewer.Services.Legado.Models;
using Uviewer.Services.Legado.BookSources;
using BookSourceModel = Uviewer.Services.Legado.Models.BookSource;

// ============================================================
// 步骤 0/1/2/3 性能基准：执行效率（耗时）+ 资源占用（分配）
// 用法：dotnet run -c Release [-- filter]
// ============================================================

static class Bench
{
    static async Task Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        var sample = FindSample();
        Console.WriteLine($"Sample: {sample} ({new FileInfo(sample).Length / 1024 / 1024} MB)");
        Console.WriteLine($"PROC={Environment.ProcessorCount} core(s)");
        Console.WriteLine();

        if (filter is null or "p0") await Phase0(sample);
        if (filter is null or "p1") await Phase1(sample);
        if (filter is null or "p2") await Phase2(sample);
        if (filter is null or "p3") await Phase3();
        if (filter is null or "p4") Phase4();
        if (filter is null or "p5") Phase5();
    }

    static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            foreach (var cand in new[]
            {
                Path.Combine(d.FullName, "Sample", "bookSource_202610192010.json"),
                Path.Combine(d.FullName, "..", "Sample", "bookSource_202610192010.json"),
            })
            {
                if (File.Exists(cand)) return Path.GetFullPath(cand);
            }
        }
        throw new FileNotFoundException("Sample json not found");
    }

    // ---------- 度量工具 ----------

    static long AllocBefore;

    static void StartMeasure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AllocBefore = GC.GetTotalAllocatedBytes(false);
    }

    static (double Ms, double Mb) EndMeasure(Stopwatch sw)
    {
        sw.Stop();
        var alloc = (GC.GetTotalAllocatedBytes(false) - AllocBefore) / 1024.0 / 1024.0;
        return (sw.Elapsed.TotalMilliseconds, alloc);
    }

    static async Task<(double Ms, double Mb, T Result)> MeasureAsync<T>(Func<Task<T>> body)
    {
        StartMeasure();
        var sw = Stopwatch.StartNew();
        var r = await body();
        var (ms, mb) = EndMeasure(sw);
        return (ms, mb, r);
    }

    static (double Ms, double Mb, T Result) Measure<T>(Func<T> body)
    {
        StartMeasure();
        var sw = Stopwatch.StartNew();
        var r = body();
        var (ms, mb) = EndMeasure(sw);
        return (ms, mb, r);
    }

    static void Row(string name, double ms, double mb, string? note = null)
    {
        Console.WriteLine($"{name,-42} {ms,9:F1} ms {mb,9:F1} MB  {note ?? ""}");
    }

    /// <summary>取 <paramref name="reps"/> 次中的<b>最小</b>耗时（CPU 密集场景下比均值稳定，GC 抖动被滤掉）。</summary>
    static (double Ms, double Mb) Best(Action body, int reps = 5)
    {
        var bestMs = double.MaxValue;
        var mbAtBest = 0.0;
        for (var i = 0; i < reps; i++)
        {
            StartMeasure();
            var sw = Stopwatch.StartNew();
            body();
            var (ms, mb) = EndMeasure(sw);
            if (ms < bestMs)
            {
                bestMs = ms;
                mbAtBest = mb;
            }
        }

        return (bestMs, mbAtBest);
    }

    /// <summary>
    /// A/B 对照：同一份工作分别交给"旧实现"和"新实现"，打印耗时、分配与加速比。
    /// 两个委托必须做<b>等价的工作量</b>，并消费结果（避免被 JIT 消除）。
    /// </summary>
    static void Ab(string name, Action oldImpl, Action newImpl, int reps = 5)
    {
        var o = Best(oldImpl, reps);
        var n = Best(newImpl, reps);
        Console.WriteLine(
            $"{name,-38} 旧 {o.Ms,7:F2} ms {o.Mb,7:F1} MB | 新 {n.Ms,7:F2} ms {n.Mb,7:F1} MB | ×{o.Ms / Math.Max(n.Ms, 0.0001):F2}");
    }

    // ============================================================
    // 阶段 0：导入 / 落盘 / 加载
    // ============================================================

    static async Task Phase0(string sample)
    {
        Console.WriteLine("== 阶段 0：模型与存储 ==");
        var json = await File.ReadAllTextAsync(sample);
        var tmp = Path.Combine(Path.GetTempPath(), "bench_bs.json");

        // 预热 JIT（用合法小切片，截断前缀是非法 JSON 会抛异常）
        var warmSlice = new List<BookSourceModel>();
        await using (var stream = File.OpenRead(sample))
        {
            await foreach (var s in BookSourceSerializer.EnumerateBookSourcesAsync(stream))
            {
                warmSlice.Add(s);
                if (warmSlice.Count >= 20) break;
            }
        }
        var warmJson = BookSourceSerializer.SerializeList(warmSlice);
        var warm = new BookSourceManager(tmp);
        await warm.ImportFromJsonAsync(warmJson);
        warm.Clear();

        for (var round = 1; round <= 2; round++)
        {
            // 导入（内存 JSON）+ 落盘
            {
                var m = new BookSourceManager(tmp);
                var (ms, mb, r) = await MeasureAsync(() => m.ImportFromJsonAsync(json));
                Row($"[{round}] ImportFromJsonAsync + Save", ms, mb, $"added={r.Added} updated={r.Updated}");
                m.Clear();
            }

            // 导入（文件流式）+ 落盘
            {
                var m = new BookSourceManager(tmp);
                var (ms, mb, r) = await MeasureAsync(() => m.ImportFromFileAsync(sample));
                Row($"[{round}] ImportFromFileAsync + Save", ms, mb, $"added={r.Added}");
                m.Clear();
            }

            // 加载
            {
                var m = new BookSourceManager(tmp);
                var (ms, mb, r) = await MeasureAsync(async () => { await m.LoadAsync(); return m.Count; });
                Row($"[{round}] LoadAsync", ms, mb, $"count={r}");
            }
        }
        File.Delete(tmp);
        Console.WriteLine();
    }

    // ============================================================
    // 阶段 1：搜索规则解析
    // ============================================================

    static async Task Phase1(string sample)
    {
        Console.WriteLine("== 阶段 1：搜索解析 ==");
        var sources = await LoadSources(sample);

        // JSON 型搜索体（200 本书）
        var sbJson = new StringBuilder("{\"data\":{\"books\":[");
        for (var i = 0; i < 200; i++)
        {
            if (i > 0) sbJson.Append(',');
            sbJson.Append($"{{\"id\":\"/novel/{i}\",\"title\":\"书名{i}\",\"author\":\"作者{i}\",\"intro\":\"简介{i}\",\"kind\":\"玄幻\",\"lastChapter\":\"第{i}章\",\"updateTime\":\"2026-10-2{i}\",\"wordCount\":{1000 + i}}}");
        }
        sbJson.Append("]}}");
        var searchJson = sbJson.ToString();

        // HTML 型搜索体（200 条）
        var sbHtml = new StringBuilder("<html><body><ul class=\"book-list\">");
        for (var i = 0; i < 200; i++)
        {
            sbHtml.Append($"<li class=\"book-item\"><a href=\"/novel/{i}\"><img src=\"/cover/{i}.jpg\"/><p class=\"name\">书名{i}</p><p class=\"author\">作者{i}</p><p class=\"intro\">简介{i}</p><span class=\"kind\">玄幻</span><span class=\"last\">第{i}章</span></a></li>");
        }
        sbHtml.Append("</ul></body></html>");
        var searchHtml = sbHtml.ToString();

        var jsonSource = new BookSourceModel
        {
            BookSourceUrl = "https://bench.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "data.books[*]",
                Name = "title",
                Author = "author",
                Intro = "intro",
                Kind = "kind",
                LastChapter = "lastChapter",
                UpdateTime = "updateTime",
                WordCount = "wordCount",
                BookUrl = "id",
                CoverUrl = "/cover/{{$.id}}.jpg",
            },
        };
        var htmlSource = new BookSourceModel
        {
            BookSourceUrl = "https://bench.example.com",
            RuleSearch = new SearchRule
            {
                BookList = "@css:.book-list>li",
                Name = "@css:.name@text",
                Author = "@css:.author@text",
                Intro = "@css:.intro@text",
                Kind = "@css:.kind@text",
                LastChapter = "@css:.last@text",
                BookUrl = "a@href",
                CoverUrl = "img@src",
            },
        };

        // 预热
        new RuleEngine().SearchBooks(jsonSource, searchJson, "https://bench.example.com");
        new RuleEngine().SearchBooks(htmlSource, searchHtml, "https://bench.example.com");

        for (var round = 1; round <= 3; round++)
        {
            var (ms1, mb1, books) = Measure(() => new RuleEngine().SearchBooks(jsonSource, searchJson, "https://bench.example.com"));
            Row($"[{round}] SearchBooks JSON 200 本", ms1, mb1, $"books={books.Count} first={books[0].Name}");

            var (ms2, mb2, books2) = Measure(() => new RuleEngine().SearchBooks(htmlSource, searchHtml, "https://bench.example.com"));
            Row($"[{round}] SearchBooks HTML 200 本", ms2, mb2, $"books={books2.Count}");
        }

        // 组合符拆分热路径（长规则 × 多片段）
        {
            var comboSrc = new BookSourceModel
            {
                BookSourceUrl = "https://bench.example.com",
                RuleSearch = new SearchRule
                {
                    BookList = "data.books[*]",
                    Name = "@json:$.title||@json:$.name##\\s##",
                    Author = "author&&kind||intro",
                    BookUrl = "id",
                },
            };
            var (ms, mb, books) = Measure(() => new RuleEngine().SearchBooks(comboSrc, searchJson, "https://bench.example.com"));
            Row("SearchBooks 组合符 JSON 200 本", ms, mb, $"books={books.Count}");
        }

        // 全量 Sample 规则健壮性（不抛异常）
        {
            var (ms, mb, n) = Measure(() =>
            {
                var html = "<html><body><div class='a'><a href='/x'>t</a></div></body></html>";
                var jsonBody = "{\"data\":{\"list\":[{\"id\":1,\"title\":\"t\"}]},\"list\":[{\"id\":1}]}";
                var count = 0;
                foreach (var s in sources.Where(s => s.RuleSearch is not null))
                {
                    count++;
                    new RuleEngine().SearchBooks(s, html, s.BookSourceUrl);
                    new RuleEngine().SearchBooks(s, jsonBody, s.BookSourceUrl);
                }
                return count;
            });
            Row($"Sample 全部搜索源 × 2 种体", ms, mb, $"sources={n}");
        }
        Console.WriteLine();
    }

    static async Task<List<BookSourceModel>> LoadSources(string sample)
    {
        var m = new BookSourceManager(Path.Combine(Path.GetTempPath(), "bench_load.json"));
        await m.ImportFromFileAsync(sample);
        var list = m.GetAll().ToList();
        m.Clear();
        File.Delete(Path.Combine(Path.GetTempPath(), "bench_load.json"));
        return list;
    }

    // ============================================================
    // 阶段 2：目录 / 正文 / 批量
    // ============================================================

    static async Task Phase2(string sample)
    {
        Console.WriteLine("== 阶段 2：详情 / 目录 / 正文 ==");

        // ---- 大 JSON 目录（2000 章，~700KB）----
        var sbToc = new StringBuilder("{\"data\":{\"chapter_lists\":[");
        for (var i = 0; i < 2000; i++)
        {
            if (i > 0) sbToc.Append(',');
            sbToc.Append($"{{\"id\":\"/ch/{i}\",\"title\":\"第{i}章 标题\",\"words\":{2000 + i}}}");
        }
        sbToc.Append("]}}");
        var tocJson = sbToc.ToString();
        Console.WriteLine($"tocJson size = {tocJson.Length / 1024} KB");

        var jsonTocSource = new BookSourceModel
        {
            BookSourceUrl = "https://api-bc.wtzw.com",
            RuleToc = new TocRule
            {
                ChapterList = "data.chapter_lists",
                ChapterName = "title",
                ChapterUrl = "id",
                UpdateTime = "{{$.words}} 字",
                NextTocUrl = "data.next",
            },
        };

        // ---- 大 HTML 目录（2000 章）----
        var sbTocHtml = new StringBuilder("<html><body><div id=\"list\">");
        for (var i = 0; i < 2000; i++)
        {
            sbTocHtml.Append($"<li><a href=\"/ch/{i}.html\">第{i}章 标题</a></li>");
        }
        sbTocHtml.Append("</div></body></html>");
        var tocHtml = sbTocHtml.ToString();

        var htmlTocSource = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleToc = new TocRule
            {
                ChapterList = "@css:#list>li",
                ChapterName = "a@text",
                ChapterUrl = "a@href",
            },
        };

        // ---- 详情（JSON，含 init）----
        var bookInfoJson = "{\"data\":{\"book\":{\"id\":\"1001\",\"title\":\"测试书名\",\"author\":\"作者甲\",\"image_link\":\"/img/1001.jpg\",\"intro\":\"简介\",\"latest_chapter_title\":\"最后一章\",\"toc_url\":\"/toc/1001.html\"}}}";
        var infoSource = new BookSourceModel
        {
            BookSourceUrl = "https://api-bc.wtzw.com",
            RuleBookInfo = new BookInfoRule
            {
                Init = "data.book",
                Name = "title",
                Author = "author",
                CoverUrl = "image_link",
                Intro = "intro",
                LastChapter = "latest_chapter_title",
                TocUrl = "toc_url",
            },
        };

        // ---- 正文（HTML，100KB，含图片与重复标题）----
        var sbContent = new StringBuilder("<html><body><div id=\"content\"><h2>第一章</h2>");
        for (var i = 0; i < 500; i++)
        {
            sbContent.Append($"<p>第{i}段正文内容，包含一些文字。<img src=\"/img/{i % 50}.jpg\"/></p>");
        }
        sbContent.Append("</div></body></html>");
        var contentHtml = sbContent.ToString();
        Console.WriteLine($"contentHtml size = {contentHtml.Length / 1024} KB");

        var htmlContentSource = new BookSourceModel
        {
            BookSourceUrl = "https://h.example.com",
            RuleContent = new ContentRule
            {
                Content = "@css:#content@html",
                Title = "@css:h2@text",
                NextContentUrl = "@css:.next@href",
                ReplaceRegex = "##<script[\\s\\S]*?</script>##",
            },
        };

        var jsonContentSource = new BookSourceModel
        {
            BookSourceUrl = "https://api-bc.wtzw.com",
            RuleContent = new ContentRule { Content = "data.content", Title = "data.title", NextContentUrl = "data.next" },
        };

        // JS 型正文（前导 <js> + formatJs 目录）——验证 Jint 引擎复用
        var jsContentSource = new BookSourceModel
        {
            BookSourceUrl = "https://js.example.com",
            RuleContent = new ContentRule
            {
                Content = "<js>var t = result.trim(); t</js>@css:#content@html",
                Title = "@css:h2@text",
            },
        };

        // 预热
        new RuleEngine().ParseBookInfo(infoSource, bookInfoJson, "https://api-bc.wtzw.com", null);
        new RuleEngine().ParseTocPage(jsonTocSource, tocJson, "https://api-bc.wtzw.com");
        new RuleEngine().ParseTocPage(htmlTocSource, tocHtml, "https://h.example.com");
        new RuleEngine().ParseContent(htmlContentSource, contentHtml, "https://h.example.com/ch/1.html");
        new RuleEngine().ParseContent(jsonContentSource, "{\"data\":{\"title\":\"t\",\"content\":\"<p>x</p>\",\"next\":null}}", "https://api-bc.wtzw.com/ch/1");
        new RuleEngine().ParseContent(jsContentSource, contentHtml, "https://js.example.com/ch/1.html");

        for (var round = 1; round <= 3; round++)
        {
            var (ms0, mb0, info) = Measure(() => new RuleEngine().ParseBookInfo(infoSource, bookInfoJson, "https://api-bc.wtzw.com", null));
            Row($"[{round}] ParseBookInfo JSON", ms0, mb0, $"name={info!.Name}");

            var (ms1, mb1, toc) = Measure(() => new RuleEngine().ParseTocPage(jsonTocSource, tocJson, "https://api-bc.wtzw.com"));
            Row($"[{round}] ParseTocPage JSON 2000 章", ms1, mb1, $"chapters={toc.Chapters.Count} next={toc.NextUrls.Count}");

            var (ms2, mb2, toc2) = Measure(() => new RuleEngine().ParseTocPage(htmlTocSource, tocHtml, "https://h.example.com"));
            Row($"[{round}] ParseTocPage HTML 2000 章", ms2, mb2, $"chapters={toc2.Chapters.Count}");

            var (ms3, mb3, _) = Measure(() => new RuleEngine().ParseContent(htmlContentSource, contentHtml, "https://h.example.com/ch/1.html"));
            Row($"[{round}] ParseContentPage HTML 100KB", ms3, mb3);

            var (ms4, mb4, _) = Measure(() =>
            {
                var body = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>正文</p><p>更多</p>\",\"next\":null}}";
                for (var i = 0; i < 500; i++) new RuleEngine().ParseContent(jsonContentSource, body, $"https://api-bc.wtzw.com/ch/{i}");
                return 0;
            });
            Row($"[{round}] ParseContentPage JSON ×500", ms4, mb4);

            var (ms5, mb5, _) = Measure(() =>
            {
                for (var i = 0; i < 100; i++) new RuleEngine().ParseContent(jsContentSource, contentHtml, "https://js.example.com/ch/1.html");
                return 0;
            });
            Row($"[{round}] ParseContentPage JS ×100", ms5, mb5);
        }

        // ---- 编排层：批量正文（fixture fetcher，200 章）----
        {
            var map = new Dictionary<string, string>();
            var chapters = new List<TocItem>();
            for (var i = 0; i < 200; i++)
            {
                var url = $"https://api-bc.wtzw.com/ch/{i}";
                map[$"/ch/{i}"] = $"{{\"data\":{{\"title\":\"第{i}章\",\"content\":\"<p>内容{i}</p><p>第二段</p><img src=\\\"/img/{i}.jpg\\\"/>\",\"next\":null}}}}";
                chapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
            }

            var fetcher = new MapFetcher(map);
            var engine = new BookSourceEngine(fetcher);
            // 预热
            await engine.GetContentsAsync(jsonContentSource, chapters.Take(4).ToList(), null, 4, null);

            for (var round = 1; round <= 2; round++)
            {
                var (ms, mb, list) = await MeasureAsync(() => engine.GetContentsAsync(jsonContentSource, chapters, null, 16, null));
                Row($"[{round}] GetContentsAsync 200 章 (JSON)", ms, mb, $"got={list.Count}");
            }

            // 并行度验证：同一负载 concurrency=1 vs 16（结果必须一致）
            {
                var (ms1, _, list1) = await MeasureAsync(() => engine.GetContentsAsync(jsonContentSource, chapters, null, 1, null));
                Row("GetContentsAsync 并发=1（串行对照）", ms1, 0, $"got={list1.Count}");
                var (ms16, _, list16) = await MeasureAsync(() => engine.GetContentsAsync(jsonContentSource, chapters, null, 16, null));
                Row("GetContentsAsync 并发=16", ms16, 0, $"got={list16.Count} 加速={ms1 / ms16:F1}x");
                AssertSame(list1, list16);
            }

            var jsChapters = new List<TocItem>();
            var jsMap = new Dictionary<string, string>();
            for (var i = 0; i < 100; i++)
            {
                var url = $"https://js.example.com/ch/{i}";
                jsMap[$"/ch/{i}"] = $"<html><body><div id=\"content\"><h2>第{i}章</h2><p>内容{i}</p><img src=\"/img/{i}.jpg\"/></div></body></html>";
                jsChapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
            }
            var jsFetcher = new MapFetcher(jsMap);
            var jsEngine = new BookSourceEngine(jsFetcher);
            await jsEngine.GetContentsAsync(jsContentSource, jsChapters.Take(4).ToList(), null, 4, null);

            for (var round = 1; round <= 2; round++)
            {
                var (ms, mb, list) = await MeasureAsync(() => jsEngine.GetContentsAsync(jsContentSource, jsChapters, null, 16, null));
                Row($"[{round}] GetContentsAsync 100 章 (JS)", ms, mb, $"got={list.Count}");
            }

            // 单章正文（含分页 + 图片抽取）
            var pageMap = new Dictionary<string, string>
            {
                ["/ch/1"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>A</p><img src=\\\"/img/a.jpg\\\"/>\",\"next\":\"/ch/1_2\"}}",
                ["/ch/1_2"] = "{\"data\":{\"title\":\"第一章\",\"content\":\"<p>B</p>\",\"next\":\"/ch/2\"}}",
            };
            var pageEngine = new BookSourceEngine(new MapFetcher(pageMap));
            var (msC, mbC, content) = await MeasureAsync(() => pageEngine.GetContentAsync(jsonContentSource, "https://api-bc.wtzw.com/ch/1"));
            Row("GetContentAsync 分页+图片", msC, mbC, $"len={content!.Content.Length} imgs={content.ImageUrls.Count}");
        }

        // ---- 目录分页（多 URL 并行）----
        {
            var map = new Dictionary<string, string>();
            for (var p = 1; p <= 8; p++)
            {
                var sb = new StringBuilder("{\"data\":{\"chapter_lists\":[");
                for (var i = 0; i < 250; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"{{\"id\":\"/ch/{p}_{i}\",\"title\":\"第{p}-{i}章\"}}");
                }
                // 注意：非插值串，}} 保持两个字面花括号
                sb.Append("],\"nexts\":[\"/toc/" + (p * 2) + "\",\"/toc/" + (p * 2 + 1) + "\"]}}");
                map[$"/toc/{p}"] = sb.ToString();
            }
            var src = new BookSourceModel
            {
                BookSourceUrl = "https://api-bc.wtzw.com",
                RuleToc = new TocRule
                {
                    ChapterList = "data.chapter_lists",
                    ChapterName = "title",
                    ChapterUrl = "id",
                    NextTocUrl = "@json:$.data.nexts[*]",
                },
            };
            var engine = new BookSourceEngine(new MapFetcher(map));
            await engine.GetTocAsync(src, "https://api-bc.wtzw.com/toc/1");
            var (ms, mb, toc) = await MeasureAsync(() => engine.GetTocAsync(src, "https://api-bc.wtzw.com/toc/1"));
            Row("GetTocAsync 多页并行 2000 章", ms, mb, $"chapters={toc.Count}");
        }
        Console.WriteLine();
    }

    // ============================================================
    // 阶段 3：正文净化管线（ReplaceRule）
    // ============================================================

    static async Task Phase3()
    {
        Console.WriteLine("== 阶段 3：正文净化管线 ==");

        // 50 条混合规则：25 正则 + 25 字面；scope 覆盖全部形态
        var rules = new List<ReplaceRule>();
        for (var i = 0; i < 50; i++)
        {
            var scope = i switch
            {
                < 10 => "*",
                < 20 => "source:https://api-bc.wtzw.com",
                < 30 => $"书名{i % 7}",
                < 40 => $"书名{i % 7};https://api-bc.wtzw.com/book/1",
                _ => null,
            };
            rules.Add(new ReplaceRule
            {
                Id = i + 1,
                Name = $"rule{i}",
                Pattern = i % 2 == 0 ? $"<p class=\"ad{i}\">[^<]*</p>" : $"广告{i}",
                Replacement = i % 2 == 0 ? "" : $"[净{i}]",
                Scope = scope,
                IsRegex = i % 2 == 0,
                Order = i,
            });
        }

        var purifier = new ContentPurifier { Rules = rules };
        var ctx = new PurifyContext("书名3", "https://api-bc.wtzw.com/book/1", "https://api-bc.wtzw.com");

        // 预热（编译快照 + 正则缓存 + JIT）
        purifier.Apply("<p class=\"ad0\">x</p>广告1", ctx);

        // ① 直接 Apply：500 次 × ~2KB 正文（快照复用 + 正则缓存路径）
        var body = new StringBuilder("<div id=\"content\">");
        for (var i = 0; i < 100; i++) body.Append($"<p>第{i}段正文<p class=\"ad{i % 50}\">广告{i}</p>广告{i % 50}</p>");
        body.Append("</div>");
        var text = body.ToString();

        for (var round = 1; round <= 3; round++)
        {
            var (ms, mb, outLen) = Measure(() =>
            {
                var len = 0;
                for (var i = 0; i < 500; i++) len += purifier.Apply(text, ctx).Length;
                return len;
            });
            Row($"[{round}] Apply ×500（50 条规则，{text.Length / 1024}KB 正文）", ms, mb, $"outLen={outLen}");
        }

        // ② 快路径：无规则引擎整体跳过（HasRules=false）
        {
            var empty = new ContentPurifier();
            var (ms, mb, same) = Measure(() =>
            {
                var n = 0;
                for (var i = 0; i < 500; i++) n += ReferenceEquals(empty.Apply(text, ctx), text) ? 1 : 0;
                return n;
            });
            Row("Apply ×500（无规则快路径，应 Same(text)）", ms, mb, $"sameRef={same}/500");
        }

        // ③ 端到端：引擎 + fixture，串行 vs 16 并行（输出必须一致）
        {
            var source = new BookSourceModel
            {
                BookSourceUrl = "https://api-bc.wtzw.com",
                RuleContent = new ContentRule { Content = "data.content", Title = "data.title" },
            };
            var map = new Dictionary<string, string>();
            var chapters = new List<TocItem>();
            for (var i = 0; i < 200; i++)
            {
                var url = $"https://api-bc.wtzw.com/ch/{i}";
                map[$"/ch/{i}"] = $"{{\"data\":{{\"title\":\"第{i}章\",\"content\":\"<p>内容{i}</p><p class=\\\"ad{i % 50}\\\">广告{i}</p>广告{i % 50}\"}}}}";
                chapters.Add(new TocItem { Index = i, ChapterName = $"第{i}章", ChapterUrl = url });
            }

            var engine = new BookSourceEngine(new MapFetcher(map), purifier);
            await engine.GetContentsAsync(source, chapters.Take(4).ToList(), "https://api-bc.wtzw.com/book/1", 4, null);

            var (ms1, _, list1) = await MeasureAsync(() => engine.GetContentsAsync(source, chapters, "https://api-bc.wtzw.com/book/1", 1, null));
            Row("GetContentsAsync 200 章 + 净化（并发=1）", ms1, 0, $"got={list1.Count}");
            var (ms16, mb16, list16) = await MeasureAsync(() => engine.GetContentsAsync(source, chapters, "https://api-bc.wtzw.com/book/1", 16, null));
            Row("GetContentsAsync 200 章 + 净化（并发=16）", ms16, mb16, $"got={list16.Count} 加速={ms1 / ms16:F1}x");
            AssertSame(list1, list16);
            Console.WriteLine($"[check] 串行/并行输出逐项一致 ✓；示例: {list16[0].Content}");

            // ④ 无 purifier 对照（同一负载，量化净化开销占比）
            var plainEngine = new BookSourceEngine(new MapFetcher(map));
            var (msPlain, _, listPlain) = await MeasureAsync(() => plainEngine.GetContentsAsync(source, chapters, "https://api-bc.wtzw.com/book/1", 16, null));
            Row("GetContentsAsync 200 章（无净化对照）", msPlain, 0, $"got={listPlain.Count}");
            if (listPlain[0].Content == list16[0].Content)
            {
                Console.WriteLine("[warn] 净化未改变输出（规则未命中？）");
            }
        }
        Console.WriteLine();
    }

    private sealed class MapFetcher : IBookSourceFetcher
    {
        private readonly Dictionary<string, string> _map;
        public MapFetcher(Dictionary<string, string> map) => _map = map;

        public Task<(string Content, string BaseUrl)> FetchAsync(
            string url, RequestSpec? spec = null, string? sourceKey = null, CancellationToken cancellationToken = default)
        {
            foreach (var kv in _map)
            {
                if (url.Contains(kv.Key, StringComparison.Ordinal)) return Task.FromResult((kv.Value, url));
            }
            return Task.FromResult((string.Empty, url));
        }

        public Task<byte[]?> FetchBytesAsync(
            string url, string? sourceKey = null, string? referer = null, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    static void AssertSame(List<ChapterContent> a, List<ChapterContent> b)
    {
        if (a.Count != b.Count) throw new InvalidOperationException($"count mismatch {a.Count} vs {b.Count}");
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Title != b[i].Title || a[i].Content != b[i].Content || a[i].ImageUrls.Count != b[i].ImageUrls.Count)
            {
                throw new InvalidOperationException($"item {i} mismatch");
            }
        }
    }

    // ============================================================
    // 阶段 4：优化专项 A/B 对照
    //   同一进程、同一份数据，把"优化前的实现"与"现在的实现"各跑一遍。
    //   旧实现按优化前的源码原样复制在本文件里（只为本对照服务，不参与产品逻辑）。
    // ============================================================

    /// <summary>结果汇总点：防止 JIT 把被测代码当无副作用死代码消除。</summary>
    static long Sink;

    static void Phase4()
    {
        Console.WriteLine("== 阶段 4：优化专项 A/B ==");

        // ---- 4.1 规则模式识别：内容嗅探是否惰性 ----
        // 旧实现：不管规则带不带前缀，都先 Trim + 尝试解析整份内容（参数总是先求值）。
        // 新实现：带前缀的规则完全不碰内容；无前缀规则才嗅探，且嗅探零分配。
        var big = BuildHtml(900 * 1024);
        var bigJson = BuildJson(900 * 1024);
        const int detectRounds = 2000;

        Ab($"DetectMode 带前缀 ×{detectRounds}（900KB HTML）",
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += OldDetectMode("@css:div#content@textNodes", big) == RuleMode.Css ? 1 : 0;
                Sink += n;
            },
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += RuleAnalyzer.DetectMode("@css:div#content@textNodes", big) == RuleMode.Css ? 1 : 0;
                Sink += n;
            });

        Ab($"DetectMode 无前缀 ×{detectRounds}（900KB HTML）",
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += OldDetectMode("div#content@textNodes", big) == RuleMode.Css ? 1 : 0;
                Sink += n;
            },
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += RuleAnalyzer.DetectMode("div#content@textNodes", big) == RuleMode.Css ? 1 : 0;
                Sink += n;
            });

        // JSON 书源是最坏情形：旧实现每条无前缀规则都要把整份 JSON 解析一遍
        Ab($"DetectMode 无前缀 ×{detectRounds}（900KB JSON）",
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += OldDetectMode("data.content", bigJson) == RuleMode.JsonPath ? 1 : 0;
                Sink += n;
            },
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += RuleAnalyzer.DetectMode("data.content", bigJson) == RuleMode.JsonPath ? 1 : 0;
                Sink += n;
            });

        // "这条规则是不是 JS"：只能由前缀决定，内容嗅探纯属白做（每章正文都要判一次）
        Ab($"JS 判定 ×{detectRounds}（900KB JSON）",
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += OldDetectMode("$.data.content", bigJson) == RuleMode.Js ? 1 : 0;
                Sink += n;
            },
            () =>
            {
                var n = 0;
                for (var i = 0; i < detectRounds; i++) n += RuleAnalyzer.IsJsRule("$.data.content") ? 1 : 0;
                Sink += n;
            });

        // ---- 4.2 textNodes 拼接：List<string>+Join vs StringBuilder ----
        var contentHtml = BuildHtml(60 * 1024);
        var doc = Selectors.Load(contentHtml);
        var node = doc.DocumentNode.SelectSingleNode("//div[@id='content']") ?? doc.DocumentNode;
        const int joinRounds = 200;

        Ab($"textNodes 拼接 ×{joinRounds}（{contentHtml.Length / 1024}KB 正文）",
            () =>
            {
                var n = 0;
                for (var i = 0; i < joinRounds; i++) n += OldTextNodes(node).Length;
                Sink += n;
            },
            () =>
            {
                var n = 0;
                for (var i = 0; i < joinRounds; i++) n += NewTextNodes(node).Length;
                Sink += n;
            });

        // ---- 4.3 净化正则：解释执行 vs 预编译 ----
        // 净化规则在"每章 × 每条规则"上反复 Replace，解释执行的正则匹配器每次都要重新解析模式。
        var patterns = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            patterns.Add((i % 5) switch
            {
                0 => $"<p class=\"ad{i}\">[^<]*</p>",
                1 => $"(?m)^广告{i}.*$",
                2 => $"[\\s\\S]*?<!--ad{i}-->[\\s\\S]*?",
                3 => $"(第{i}章)[\\s\\u3000]*",
                _ => $"(?i)<script[^>]*>ad{i}[\\s\\S]*?</script>",
            });
        }

        var chunk = new StringBuilder();
        for (var i = 0; i < 120; i++) chunk.Append($"<p>第{i}段正文正文正文正文正文</p><p class=\"ad{i % 25}\">广告</p>\n");
        var corpus = chunk.ToString();

        var interpreted = patterns.Select(p => new Regex(p, RegexOptions.Multiline)).ToArray();
        var compiled = patterns.Select(p => new Regex(p, RegexOptions.Multiline | RegexOptions.Compiled)).ToArray();
        const int purifyRounds = 200;

        Ab($"净化正则 25 条 ×{purifyRounds} 轮（{corpus.Length / 1024}KB）",
            () =>
            {
                var len = 0;
                for (var r = 0; r < purifyRounds; r++)
                {
                    var t = corpus;
                    foreach (var re in interpreted) t = re.Replace(t, string.Empty);
                    len += t.Length;
                }

                Sink += len;
            },
            () =>
            {
                var len = 0;
                for (var r = 0; r < purifyRounds; r++)
                {
                    var t = corpus;
                    foreach (var re in compiled) t = re.Replace(t, string.Empty);
                    len += t.Length;
                }

                Sink += len;
            });

        // ---- 4.4 行内 ## 规则正则：Singleline 解释 vs 编译 ----
        var rulePatterns = new[] { "<[^>]+>", "&nbsp;", "(?m)^\\s+", "[\\u3000\\s]{2,}" };
        var ruleInterpreted = rulePatterns.Select(p => new Regex(p, RegexOptions.Singleline)).ToArray();
        var ruleCompiled = rulePatterns.Select(p => new Regex(p, RegexOptions.Singleline | RegexOptions.Compiled)).ToArray();
        const int ruleRounds = 500;

        Ab($"行内规则正则 4 条 ×{ruleRounds} 轮",
            () =>
            {
                var len = 0;
                for (var r = 0; r < ruleRounds; r++)
                {
                    var t = corpus;
                    foreach (var re in ruleInterpreted) t = re.Replace(t, string.Empty);
                    len += t.Length;
                }

                Sink += len;
            },
            () =>
            {
                var len = 0;
                for (var r = 0; r < ruleRounds; r++)
                {
                    var t = corpus;
                    foreach (var re in ruleCompiled) t = re.Replace(t, string.Empty);
                    len += t.Length;
                }

                Sink += len;
            });

        Console.WriteLine($"(sink={Sink})");
        Console.WriteLine();
    }

    /// <summary>优化前的模式识别（Trim 整份内容 + 总是嗅探 JSON）。</summary>
    static RuleMode OldDetectMode(string rule, string content)
    {
        var ct = content.Trim();
        var isJson = false;
        if (ct.Length > 0 && (ct[0] == '{' || ct[0] == '['))
        {
            try
            {
                using var _ = JsonDocument.Parse(ct);
                isJson = true;
            }
            catch
            {
                isJson = false;
            }
        }

        return RuleAnalyzer.DetectModeCore(rule, isJson);
    }

    /// <summary>优化前的 textNodes：先攒 List&lt;string&gt; 再 Join。</summary>
    static string OldTextNodes(HtmlNode node)
    {
        var texts = new List<string>();
        Walk(node, texts);
        return string.Join("\n", texts);

        static void Walk(HtmlNode n, List<string> acc)
        {
            foreach (var child in n.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    var s = child.InnerText.Trim();
                    if (s.Length > 0) acc.Add(s);
                }
                else if (child.NodeType == HtmlNodeType.Element)
                {
                    Walk(child, acc);
                }
            }
        }
    }

    /// <summary>优化后的 textNodes：直接写 StringBuilder（等价算法，供 A/B 对照）。</summary>
    static string NewTextNodes(HtmlNode node)
    {
        var sb = new StringBuilder();
        Walk(node, sb);
        return sb.ToString();

        static void Walk(HtmlNode n, StringBuilder acc)
        {
            foreach (var child in n.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    var s = child.InnerText.AsSpan().Trim();
                    if (s.Length > 0)
                    {
                        if (acc.Length > 0) acc.Append('\n');
                        acc.Append(s);
                    }
                }
                else if (child.NodeType == HtmlNodeType.Element)
                {
                    Walk(child, acc);
                }
            }
        }
    }

    /// <summary>构造一份近似真实正文页的 HTML（含 <c>div#content</c> 与若干段落/广告块）。</summary>

    // ============================================================
    // 阶段 5：本地 TXT「窗口式按需净化」可行性测量
    //
    // 要回答的问题：把"打开文件时整本净化"改成"只净化当前窗口（前 2 后 5 + 本章 = 8 章）"
    // 到底能省多少等待？两个场景：
    //   ① 首次开启阅读模式（打开文件）
    //   ② 变更替换净化规则后刷新
    // 注意：目录（标题规则）必须扫全本才能得到全书章节，这部分窗口化省不掉，
    // 所以它作为"两者都要付的固定成本"单独测量。
    // ============================================================
    static void Phase5()
    {
        Console.WriteLine("== 阶段 5：本地 TXT 窗口式按需净化可行性 ==");

        var chaptersDir = FindPerfChaptersDir();
        // chaptersDir = …/PerfBenchmark/text/chapters → 上两级 = …/PerfBenchmark
        var perfBenchDir = Directory.GetParent(Directory.GetParent(chaptersDir)!.FullName)!.FullName;
        var rulesDir = Path.Combine(perfBenchDir, "rules");
        var replaceRules = LoadReplaceRules(Path.Combine(rulesDir, "replace-rules.json"));
        var titleRules = LoadTitleRules(Path.Combine(rulesDir, "title-rules.json"));

        var book = BuildBook(chaptersDir, 1_400_000);
        Console.WriteLine($"模拟书：{book.Text.Length / 1024} KB / {book.ChapterTexts.Count} 章 / 替换规则 {replaceRules.Count} 条 / 标题规则 {titleRules.Count} 条");

        var purifier = new ContentPurifier { Rules = replaceRules };
        var ctx = PurifyContext.Default;

        // 8 章窗口（前 2 后 5 + 本章），取中间位置以便两侧都满
        var center = 5;
        var window = new List<string>();
        for (var i = Math.Max(0, center - 2); i <= Math.Min(book.ChapterTexts.Count - 1, center + 5); i++)
        {
            window.Add(book.ChapterTexts[i]);
        }

        var windowBytes = window.Sum(w => w.Length);

        // 预热（JIT + 正则编译缓存）
        _ = purifier.Apply(book.Text, ctx);
        _ = TextTocBuilder.Build(book.Text, titleRules);

        var (fullMs, fullMb) = Best(() => Sink += purifier.Apply(book.Text, ctx).Length, 3);
        var (windowMs, windowMb) = Best(() =>
        {
            var n = 0;
            foreach (var slice in window) n += purifier.Apply(slice, ctx).Length;
            Sink += n;
        }, 3);
        var (tocMs, tocMb) = Best(() => Sink += TextTocBuilder.Build(book.Text, titleRules).Count, 3);
        var (oneChapterMs, oneChapterMb) = Best(() => Sink += purifier.Apply(book.ChapterTexts[center], ctx).Length, 3);

        Row($"全文净化（旧：打开时整本 {book.Text.Length / 1024}KB）", fullMs, fullMb);
        Row($"8 章窗口净化（新：只算窗口 {windowBytes / 1024}KB）", windowMs, windowMb);
        Row("单章净化（规则变更时的前台部分）", oneChapterMs, oneChapterMb);
        Row("全文目录构建（标题规则，两者都必须付）", tocMs, tocMb);

        var oldOpen = fullMs + tocMs;
        var newOpen = windowMs + tocMs;
        Console.WriteLine();
        Console.WriteLine($"打开文件：旧 ≈ {oldOpen:F0} ms（整本净化 {fullMs:F0} + 目录 {tocMs:F0}）");
        Console.WriteLine($"          新 ≈ {newOpen:F0} ms（窗口净化 {windowMs:F0} + 目录 {tocMs:F0}）");
        Console.WriteLine($"          ⇒ 省下 {oldOpen - newOpen:F0} ms（{(oldOpen > 0 ? 100 * (oldOpen - newOpen) / oldOpen : 0):F0}%），"
            + $"其中目录构建占新耗时的 {(newOpen > 0 ? 100 * tocMs / newOpen : 0):F0}%");
        Console.WriteLine();
        Console.WriteLine($"规则变更：旧（整本重净化，前台阻塞）≈ {fullMs:F0} ms");
        Console.WriteLine($"          新（本章前台 {oneChapterMs:F0} ms + 窗口后台 {windowMs:F0} ms）");
        Console.WriteLine($"          ⇒ 前台等待降低 {(fullMs > 0 ? 100 * (fullMs - oneChapterMs) / fullMs : 0):F0}%");
        Console.WriteLine();

        // 规则条数敏感性：真实用户常见 20~80 条
        foreach (var scale in new[] { 4, 8 })
        {
            var scaled = new List<ReplaceRule>();
            for (var k = 0; k < scale; k++)
            {
                foreach (var r in replaceRules)
                {
                    scaled.Add(new ReplaceRule
                    {
                        Id = r.Id + k * 1000,
                        Name = $"{r.Name}#{k}",
                        Pattern = r.Pattern,
                        Replacement = r.Replacement,
                        Scope = r.Scope,
                        IsRegex = r.IsRegex,
                        IsEnabled = true,
                        Order = r.Order + k * 100,
                    });
                }
            }

            var p2 = new ContentPurifier { Rules = scaled };
            _ = p2.Apply(book.Text, ctx);
            var (f, _) = Best(() => Sink += p2.Apply(book.Text, ctx).Length, 3);
            var (w, _) = Best(() =>
            {
                var n = 0;
                foreach (var slice in window) n += p2.Apply(slice, ctx).Length;
                Sink += n;
            }, 3);
            Console.WriteLine($"规则 {scaled.Count} 条：整本 {f:F0} ms vs 窗口 {w:F0} ms（省 {f - w:F0} ms）");
        }
    }

    /// <summary>定位 <c>Sample/PerfBenchmark/text/chapters</c>（从 bin 向上回溯）。</summary>
    static string FindPerfChaptersDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Sample", "PerfBenchmark", "text", "chapters");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Sample/PerfBenchmark/text/chapters 未找到");
    }

    static List<ReplaceRule> LoadReplaceRules(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ReplaceRule>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<ReplaceRule>();
    }

    static List<TitleRule> LoadTitleRules(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<TitleRule>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<TitleRule>();
    }

    /// <summary>用样例章节拼一本接近真实的 TXT 书（重复章节直到目标体积，标题行改成"第N章"）。</summary>
    static (string Text, List<string> ChapterTexts) BuildBook(string chaptersDir, int targetBytes)
    {
        var sources = Directory.GetFiles(chaptersDir, "*.html").OrderBy(p => p, StringComparer.Ordinal).ToList();
        var bodies = sources.Select(p => LegadoHtmlText.ToPlainText(File.ReadAllText(p)).Trim()).ToList();

        var sb = new StringBuilder(targetBytes + 4096);
        var chapters = new List<string>();
        var index = 0;
        while (sb.Length < targetBytes)
        {
            var body = bodies[index % bodies.Count];
            var chapter = $"第{index + 1}章 测试章节{index + 1}\n\n{body}";
            chapters.Add(chapter);
            sb.Append(chapter).Append("\n\n");
            index++;
        }

        return (sb.ToString(), chapters);
    }

    static string BuildHtml(int approxBytes)
    {
        var sb = new StringBuilder(approxBytes + 1024);
        sb.Append("<!DOCTYPE html><html><head><title>t</title></head><body><div id=\"content\">");
        var i = 0;
        while (sb.Length < approxBytes)
        {
            sb.Append($"<p>第{i}段正文内容，这里是一些用于压测的中文文本。</p><p class=\"ad{i % 25}\">广告{i % 25}</p>");
            i++;
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>构造一份近似真实接口响应的 JSON（单层对象 + 长数组）。</summary>
    static string BuildJson(int approxBytes)
    {
        var sb = new StringBuilder(approxBytes + 1024);
        sb.Append("{\"data\":{\"content\":\"");
        var i = 0;
        while (sb.Length < approxBytes)
        {
            sb.Append($"<p>第{i}段正文内容，这里是一些用于压测的中文文本。</p>");
            i++;
        }

        sb.Append("\",\"title\":\"t\"}}");
        return sb.ToString();
    }
}
