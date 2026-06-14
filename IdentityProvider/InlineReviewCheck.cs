namespace IdentityProvider;

// [検証用 / マージ禁止] Claude Code Review のインラインコメント投稿経路
// (mcp__github_inline_comment__create_inline_comment) を実地確認するための
// ダミーコード。意図的にバグを含めている。検証後にこのファイルごと削除する。
public static class InlineReviewCheck
{
    // バグ1: denominator が 0 のとき DivideByZeroException を送出する。
    public static int Divide(int numerator, int denominator)
    {
        return numerator / denominator;
    }

    // バグ2: value が null のとき NullReferenceException を送出する。
    public static int GetLength(string? value)
    {
        return value.Length;
    }
}
