using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace VRCToolsDataSync_App.ViewModels;

/// <summary>
/// 画面に出す問い合わせの選択肢 1 つ (issue #10)。
/// <para>
/// 押されたときに答えを返すところまでを持つ。どの値を返すかは問い合わせを
/// 出した側が閉じ込めており、ここに残るのは「ボタンに出す文字列」と
/// 「押されたら何をするか」だけである。選択肢の数と並びは問い合わせごとに
/// 変わるので、画面はこれを一覧として横に並べるだけでよい。
/// </para>
/// </summary>
public sealed class PromptOption
{
    /// <param name="label">ボタンに出す文字列。</param>
    /// <param name="choose">押されたときに答えを返す処理。</param>
    public PromptOption(string label, Action choose)
    {
        Label = label;
        ChooseCommand = new RelayCommand(choose);
    }

    public string Label { get; }

    public ICommand ChooseCommand { get; }
}
