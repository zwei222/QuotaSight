# Windows通知の受け入れ確認手順

この手動確認では、Windows通知の登録経路と画面上の表示経路をそれぞれ確認します。CIとは別の確認です。GitHub-hosted Windows runnerは昇格した管理者tokenでUACが無効の状態で実行される一方、Windows App SDK 2.5.1の`IsSupported()`は昇格プロセスでfalseを返すため、CIはNative AOTのpublishとsmoke testまでで終了します。smoke test成功や通知APIの登録成功だけを、desktop通知の表示確認として扱ってはいけません。

## 環境と準備

- 対話ログオン中の実Windows 11 x64 desktopを使用します。
- サインイン中の標準ユーザーとしてQuotaSightを起動します。昇格して実行（「管理者として実行」）しないでください。プロセスが非昇格であることを確認します。
- 確認対象commitと同一commitから作成した、自己完結型Windows x64 Native AOT ZIPを用意して、空のdirectoryへ展開します。framework-dependent buildや別commitのbuildは使用しません。
- テスト前にcommit SHAとZIPのSHA-256を記録します。Windows PowerShellでは`Get-FileHash -Algorithm SHA256 <ZIPのパス>`でZIP digestを取得できます。
- Windows通知の許可状態と集中モード（Focus assist / Do not disturb）の状態を確認して記録します。アカウント名、organization名、device名など、個人・組織・機器を識別する情報は記録しません。

## 通知登録probe

展開したZIPのdirectoryで、非昇格のterminalから実際の同梱実行ファイルを実行します。

```powershell
.\QuotaSight.UI.exe --notification-probe
$LASTEXITCODE
```

正確なexit codeと、probeが出力した固定失敗メッセージがあればそれを記録します。exit code `0`は、サポート確認、イベント購読、登録、登録解除、全通知解除、およびイベント購読解除がすべて正常に完了したことを示すプログラム上の証拠です。成功時は通常メッセージを出力しません。成功結果の文字列が出力されることを期待したり、転記したりしないでください。nonzero exit codeと固定失敗メッセージ（出力された場合）は、いずれかの処理が失敗したことを示します。このprobeはpopup表示テストではなく、exit code `0`もWindows通知の表示を証明するものではありません。

## しきい値通知の画面表示

1. Windows SettingsでQuotaSightの通知が許可されていることを確認し、観測した状態を記録します。設定を変更する場合は変更前後を記録します。
2. 集中モード（Focus assist / Do not disturb）の状態を記録します。方針上可能で表示を観測しやすくする場合はオフにし、変更前後を記録します。
3. 展開済みの`QuotaSight.UI.exe`を通常起動（非昇格）します。UIで先にしきい値通知を有効にし、テスト用しきい値を設定します。その後、freshで新規かつ一意な手動割合snapshotを追加し、その割合がしきい値に初めて到達または超過するようにします。snapshotの保存直後にOS popupを確認してください。単に通知を待つのではありません。手動snapshotによるしきい値表示は`tests/QuotaSight.UI.Tests/ThresholdNotificationPresentationTests.cs`の`Manual_snapshot_threshold_is_presented`で確認されています。手動フォームには架空のテスト用アカウント名が必要ですが、実アカウントの認証情報や本番quota観測値は不要です。
4. アプリ内bannerをOS通知として扱ってはいけません。
5. 人がWindows desktopを目視し、OS通知が表示されたかを記録します。結果は「表示を目視確認」「表示されなかった」「未テスト」のいずれかとし、個人を識別する情報を含まない短い観測メモを付けます。表示されない、または未テストの場合、実表示は未確認であることを明記します。

## 結果記録

以下のtemplateをテスト記録へコピーし、実測値を記入します。credential、token、Client ID、organization/device識別子、その他の個人・組織を識別する情報は記載しないでください。

- Commit SHA:
- Windows x64 ZIP SHA-256:
- OS edition/version/build（識別情報を含まない）:
- プロセスの昇格状態: 非昇格 / 昇格 / 不明
- QuotaSightのWindows通知許可: 許可 / ブロック / 不明
- 集中モード / Do not disturb: オン / オフ / 不明（変更した場合は変更前後）
- Probe exit code（実測値）:
- Probe固定失敗メッセージ（実測値そのまま、出力された場合）:
- Probeによる登録ライフサイクル確認（exit code 0 / 失敗 / 実証されず）:
- 手動snapshotのfreshnessとしきい値設定（識別情報を含めず簡潔に記載）:
- 人によるOS通知のpixel目視結果: 表示を目視確認 / 表示されなかった / 未テスト
- 実表示確認: 合格 / 不合格 / 未テスト
- 備考（credentialや識別名は記載しない）:

テスト記録が示すのは、記録されたcommit、ZIP hash、OS、設定に対する結果だけです。未テストまたは目視できなかった結果を成功として報告してはいけません。