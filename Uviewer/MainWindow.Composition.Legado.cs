using Uviewer.Services.Legado;

namespace Uviewer
{
    public sealed partial class MainWindow
    {
        private static partial class MainWindowComposition
        {
            /// <summary>
            /// 书源阅读功能初始化（阶段 5 集成）。
            /// <para>这里只做"接线"，不做 IO：书源/替换规则/进度的加载是异步的，
            /// 由首次调用 <c>ShowBookSourceSearchAsync</c> / <c>OpenLegadoBookAsync</c> 时
            /// 通过 <c>EnsureInitializedAsync</c> 触发（幂等 + 并发去重）。
            /// 这样启动路径不受书源文件 IO 影响（408 个源约 5MB 解析 ≈ 50ms，
            /// 但用户可能整场阅读都不开书源，不该为此阻塞首帧）。</para>
            /// </summary>
            private static class LegadoFeatureComposition
            {
                public static void Initialize(MainWindow window)
                {
                    // 预热：构造 BookSourceService 与搜索服务（纯对象分配，无 IO），
                    // 让首次搜索少一次构造开销；不触碰磁盘。
                    _ = window._documentReaderController.BookSources;
                }
            }
        }
    }
}