namespace Span.Services
{
    /// <summary>우클릭 메뉴에서 "PDF로 변환 / 합치기"를 요청받는 쪽 (MainWindow).</summary>
    public interface IPdfConvertHost
    {
        /// <param name="paths">우클릭한 파일(들). 같은 선택 범위의 다른 파일도 함께 처리한다.</param>
        /// <param name="merge">true 면 선택한 파일을 하나의 PDF 로 합친다.</param>
        void PerformConvertToPdf(string[] paths, bool merge);
    }
}
