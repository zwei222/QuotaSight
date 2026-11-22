# Windows通知の受け入れ確認手順

この手動確認では、Windows通知の登録経路と画面表示の経路を、CIとは別に確認します。GitHub-hosted Windows runnerは、UACが無効の状態で昇格済み管理者tokenを使って実行されます。一方、Windows App SDK 2.xの`IsSupported()`は昇格プロセスでfalseを返します。そのため、CIで確認するのはNative AOTのpublishとsmoke testまでです。

**このビルドの範囲。** 現行のUIが出す通知はアプリ内バナーだけです。アプリのどの経路もデスクトップ通知バックエンドを呼び出さないため、このビルドにはOS通知を送る経路がありません。しきい値通知と手動入力の利用枠スナップショットは削除済みです。実行できるのは`--notification-probe`による登録診断だけです。したがって画面表示は、UIにOS通知の契機が戻るまで**該当なし**として記録し、以前の「しきい値トースト」手順は実行不能な指示として残さず削除します。

## Windows App SDKの同梱内容（実機では未確認）

Windows版は`Microsoft.WindowsAppSDK`メタパッケージを参照しなくなりました。QuotaSightが使うのはトースト通知（`Microsoft.Windows.AppNotifications`）だけで、これは`Microsoft.WindowsAppSDK.Foundation`に含まれます。そのため`src/QuotaSight.UI/QuotaSight.UI.csproj`は`Microsoft.WindowsAppSDK.Foundation` 2.7.0と、その依存である`Microsoft.WindowsAppSDK.InteractiveExperiences` 2.7.0を直接参照しています。FoundationはInteractiveExperiencesを`[2.7.0, 3.0.0)`の範囲で要求するため、明示的な参照によって、後からrestoreしたときに2.7.xの別版へ勝手に移って同梱フレームワークが変わることを防ぎ、確認済みの1バージョンに固定しています。メタパッケージはWinUI、onnxruntime、DirectMLなどQuotaSightが読み込まないpayloadも同梱していたため、Windows ZIPには約300ファイル・約80MiBの未使用内容が含まれていました。`WindowsAppSDKSelfContained`は`true`のままです。フレームワークを同梱し続けるので、利用者PCにWindows App SDK Runtimeを別途インストールする必要はなく、Bootstrapperも使いません。

この構成はWindows以外のマシンでのrestoreと参照内容の確認までしか行っていません。Windows専用のコンパイル経路（`#if WINDOWS`）、Native AOTのpublish、生成されるファイル数はWindows CI runnerでのみ確認され、そのrunnerはトーストの実表示を観測できません。Foundation単体構成でのトースト表示とトーストのクリックは**未検証**であり、そもそもこのビルドには表示させるUI操作がありません。

## 環境と準備

- 対話ログオン中の実Windows 11 x64デスクトップ環境を使います。
- サインイン中のユーザーとしてQuotaSightを通常起動します。標準ユーザーで実行するか、管理者権限のあるユーザーを使う場合は昇格せずに実行します。「管理者として実行」は選ばず、プロセスが非昇格であることを確認します。
- 確認対象と同じコミットから作成した、自己完結型Windows x64 Native AOT ZIPを用意し、空のフォルダーに展開します。ZIPの直下には`QuotaSight-<version>-win-x64/`フォルダーが1つだけあり、実行ファイルとそのアセットはその中にあります。framework-dependent buildや別コミットのbuildは使いません。
- テスト前にコミットSHAとZIPのSHA-256を記録します。Windows PowerShellでは`Get-FileHash -Algorithm SHA256 <ZIPのパス>`でハッシュを取得できます。
- OS通知は発生しない見込みですが、環境の記述として、QuotaSightのWindows通知の許可状態と集中モード（Focus assist / Do not disturb）の状態を確認して記録します。アカウント名、organization名、device名など、個人・組織・機器を識別できる情報は記録しません。

## 通知登録probe

展開したZIPのフォルダーで、非昇格の端末から実行ファイルを起動します。

```powershell
$probe = Start-Process -FilePath .\QuotaSight.UI.exe -ArgumentList '--notification-probe' -Wait -PassThru
$probe.ExitCode
```

リリースする`QuotaSight.UI.exe`はWindowsのGUIサブシステム（WinExe）のバイナリなので、自前のコンソールを持たず、ダブルクリックしても端末は開きません。`--smoke-test`と`--notification-probe`のときだけ、起動元のコンソールが存在すればそのコンソールへ出力します。診断メッセージを見るには、エクスプローラーからではなく`cmd.exe`またはPowerShellから起動してください。またGUIサブシステムのプロセスはシェルへ即座に制御を返すため、`.\QuotaSight.UI.exe --notification-probe`を直接呼んでも待機されず、`$LASTEXITCODE`も設定されません。プロセスの終了を待ち、実際のexit codeを取得するには`Start-Process -Wait -PassThru`を使います。

正確なexit codeと、probeが固定の失敗メッセージを出力した場合はその内容を記録します。exit code `0`は、サポート確認、イベント購読、登録、登録解除、全通知登録の解除、およびイベント購読解除がすべて正常に完了したことを示すプログラム上の証拠です。ここでいう`UnregisterAll`は通知登録を解除する処理で、画面上にすでに表示された通知を消す操作ではありません。成功時は通常メッセージを出力しません。成功を示す文字列が出ると期待したり、転記したりしないでください。nonzero exit codeと固定の失敗メッセージ（出力された場合）は、いずれかの処理が失敗したことを示します。このprobeはポップアップ表示の確認ではありません。exit code `0`でもWindows通知が画面に表示されたとは証明できません。GUIサブシステム化そのものの証拠は、CIが公開済みPEヘッダーの`Subsystem == 2`（`WINDOWS_GUI`）を読み取った結果とsmoke testのexit codeだけです。実機デスクトップでexeをダブルクリックして再確認した結果ではありません。

## 画面表示（このビルドでは該当なし）

観測できる対象がありません。UI操作、更新、アラートのどれも`IDesktopNotificationBackend.TryNotifyAsync`を呼び出さず、以前の版が起こせたしきい値通知は利用枠表示とともに削除されました。削除済みの機能（しきい値設定、手動入力の割合スナップショット）で確認シナリオを組み立てず、アプリ内バナーをOS通知として扱わないでください。

この確認項目は、未確認であることが見えるように記録には残します。OS通知の契機が戻った時点で、結果を主張する前に、具体的で再現可能な操作に置き換える必要があります。

## 結果記録

以下のひな形をテスト記録にコピーし、実測値を記入します。credential、token、Client ID、organization/device識別子、その他の個人・組織を識別する情報は記載しないでください。

- Commit SHA:
- Windows x64 ZIP SHA-256:
- OS edition/version/build（識別情報を含まない）:
- プロセスの昇格状態: 非昇格 / 昇格 / 不明
- QuotaSightのWindows通知許可: 許可 / ブロック / 不明
- 集中モード / Do not disturb: オン / オフ / 不明（変更した場合は変更前後）
- Probe exit code（実測値）:
- Probe固定失敗メッセージ（実測値そのまま、出力された場合）:
- Probeによる登録ライフサイクル確認（exit code 0 / 失敗 / 実証されず）:
- OS通知の画面表示: このビルドでは該当なし（契機なし） / 表示を目視確認 / 表示されなかった / 未テスト
- 実表示確認: 該当なし / 合格 / 不合格 / 未テスト
- 備考（credentialや識別名は記載しない）:

テスト記録が示すのは、記録したコミット、ZIPのハッシュ値、OS、設定についての結果だけです。未テストまたは画面で目視できなかった結果を成功として報告してはいけません。
