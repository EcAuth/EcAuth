// 一時検証用ファイル（このPRはマージしません / ビルド対象外パス）
public class ReviewVerificationSample
{
    public string GetValue(string input)
    {
        // 意図的な問題: null チェックなし・マジックナンバー・範囲外の可能性
        return input.ToString().Substring(0, 100);
    }
}
