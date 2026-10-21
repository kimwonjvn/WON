using System;
using System.Text.RegularExpressions;

namespace Span.Helpers;

/// <summary>
/// 와일드카드 이름 패턴(* = 0개 이상 문자, ? = 정확히 1개 문자)을 전체 이름 매칭 Regex로 바꾼다.
/// 필터 바(FolderViewModel.MatchesFilter)와 검색(SearchQueryParser → SearchFilter)이 같이 쓴다.
///
/// 엔진은 패턴 모양으로 고른다.
///   뒤에 문자가 이어지는 '*'가 둘 이상(*a*b, a*b*c)
///     → NonBacktracking. 백트래킹 엔진에서 다항 시간이라 긴 이름에 수백 ms가 걸리고, Regex 기본
///       타임아웃(Issue #36, 1초)에 걸리면 예외가 된다. 필터 바는 IsMatch를 try 밖에서 불러 크래시,
///       재귀 검색은 항목별 catch가 있어 그 항목이 결과에서 조용히 빠진다. NonBacktracking은 선형
///       시간이고, 와일드카드가 만드는 .*와 .는 두 엔진에서 의미가 같다.
///   그 이하(*.txt, report*, *X*, '*' + 긴 파일명, ?만 있는 패턴)
///     → 2.0.6까지와 같은 백트래킹 엔진. 이 모양은 백트래킹에서도 O(이름 x 패턴)이다.
///       NonBacktracking은 생성 비용이 패턴의 서로 다른 문자 수에 따라 커져서('*' + 한글 255자
///       파일명 약 20ms·6MB, 긴 CJK 문단은 수 초·수백 MB) 이 모양에서는 비용만 늘어난다.
/// </summary>
internal static class WildcardRegex
{
    public static Regex Create(string wildcard)
    {
        var pattern = "^" + Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".") + "$";

        if (CountInnerStars(wildcard) < 2)
            return CreateBacktracking(pattern);

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // NonBacktracking은 오토마톤 크기에 상한이 있어 긴 패턴은 생성이 실패한다(기본 상한에서
            // 199자부터). Program.Main이 상한을 올려 두지만, 그보다 큰 패턴이나 Main을 거치지 않는
            // 호출(단위 테스트)은 여기로 온다.
            DebugLogger.Log($"[WildcardRegex] NonBacktracking unavailable (pattern {pattern.Length} chars), using backtracking");
            return CreateBacktracking(pattern);
        }
    }

    /// <summary>
    /// 2.0.6까지와 같은 엔진. 타임아웃은 명시적으로 무한 — 기본 1초(Issue #36)를 물려받으면
    /// 필터 바의 IsMatch(try 밖)가 크래시 경로가 된다.
    /// </summary>
    private static Regex CreateBacktracking(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// 뒤에 '*'가 아닌 문자가 이어지는 '*'의 수. 끝의 '*'는 세지 않고, 연속된 '*'는 하나로 센다
    /// ('**a' = 1 — Regex가 인접한 .*를 하나로 합친다).
    /// </summary>
    private static int CountInnerStars(string wildcard)
    {
        int count = 0;
        for (int i = 0; i < wildcard.Length - 1; i++)
            if (wildcard[i] == '*' && wildcard[i + 1] != '*') count++;
        return count;
    }
}
