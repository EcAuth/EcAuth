namespace IdentityProvider;

// [検証用 / マージ禁止] claude.yml（@claude 返信対話）の pull_request_review_comment
// 経路を、オープン PR + 最新 claude.yml の状況で実地確認するためのダミーコード。
// 検証後にこの PR ごとクローズ・ブランチ削除する。
public static class InlineReplyCheck
{
    // 意図的なバグ: denominator が 0 のとき DivideByZeroException を送出する。
    public static int Divide(int numerator, int denominator)
    {
        return numerator / denominator;
    }
}
