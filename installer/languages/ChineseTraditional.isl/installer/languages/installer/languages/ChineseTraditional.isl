[LangOptions]
; The following three entries are very important. Be sure to read and
; understand the '[LangOptions] section' topic in the help file.
LanguageName=繁體中文
; About LanguageID, to reference link:
; https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-lcid/a9eac961-e77d-41a6-90a5-ce1a8b0cdb9c
LanguageID=$0404
; About CodePage, to reference link:
; https://docs.microsoft.com/en-us/windows/win32/intl/code-page-identifiers
LanguageCodePage=950
; If the language you are translating to requires special font faces or
; sizes, uncomment any of the following entries and change them accordingly.
DialogFontName=Microsoft JhengHei UI
;DialogFontSize=9
;DialogFontBaseScaleWidth=7
;DialogFontBaseScaleHeight=15
WelcomeFontName=Microsoft JhengHei UI
;WelcomeFontSize=14


[Messages]


; *** Application titles
SetupAppTitle=安裝程式
SetupWindowTitle=%1 安裝程式
UninstallAppTitle=解除安裝
UninstallAppFullTitle=解除安裝 %1


; *** Misc. common
InformationTitle=資訊
ConfirmTitle=確認
ErrorTitle=錯誤


; *** SetupLdr messages
SetupLdrStartupMessage=這將會安裝 %1。您想要繼續嗎？
LdrCannotCreateTemp=無法建立暫存檔案。安裝程式將會結束
LdrCannotExecTemp=無法執行暫存檔案。安裝程式將會結束
HelpTextNote=


; *** Startup error messages
LastErrorMessage=%1。%n%n錯誤 %2：%3
SetupFileMissing=安裝資料夾中遺失檔案 %1。請修正此問題或重新取得此軟體。
SetupFileCorrupt=安裝檔案已經損毀。請重新取得此軟體。
SetupFileCorruptOrWrongVer=安裝檔案已經損毀，或與安裝程式的版本不符。請修正此問題或重新取得此軟體。
InvalidParameter=某個無效的參數已傳遞至命令列：%n%n%1
SetupAlreadyRunning=安裝程式已經在執行。
WindowsVersionNotSupported=這個程式並不支援目前在電腦所執行的 Windows 版本。
WindowsServicePackRequired=這個程式需要 %1 Service Pack %2 或更新。
NotOnThisPlatform=這個程式無法在 %1 執行。
OnlyOnThisPlatform=這個程式必須在 %1 執行。
OnlyOnTheseArchitectures=這個程式只能在專門為以下處理器架構而設計的 Windows 上安裝：%n%n%1
WinVersionTooLowError=這個程式必須在 %1 版本 %2 或以上的系統執行。
WinVersionTooHighError=這個程式無法安裝在 %1 版本 %2 或以上的系統。
AdminPrivilegesRequired=您必須登入成系統管理員以安裝這個程式。
PowerUserPrivilegesRequired=您必須登入成系統管理員或 Power Users 群組的成員以安裝這個程式。
SetupAppRunningError=安裝程式偵測到 %1 正在執行。%n%n請立即關閉它的所有執行個體，然後按 「確定」 繼續，或按 「取消」 離開。
UninstallAppRunningError=解除安裝程式偵測到 %1 正在執行。%n%n請立即關閉它的所有執行個體，然後按 「確定」 繼續，或按 「取消」 離開。


; *** Startup questions
PrivilegesRequiredOverrideTitle=選擇安裝程式安裝模式
PrivilegesRequiredOverrideInstruction=選擇安裝模式
PrivilegesRequiredOverrideText1=可以為所有使用者安裝 %1 (需要系統管理員權限)，或是僅為您安裝。
PrivilegesRequiredOverrideText2=可以僅為您安裝 %1，或是為所有使用者安裝 (需要系統管理員權限)。
PrivilegesRequiredOverrideAllUsers=為所有使用者安裝 (&A)
PrivilegesRequiredOverrideAllUsersRecommended=為所有使用者安裝 (建議選項) (&A)
PrivilegesRequiredOverrideCurrentUser=僅為我安裝 (&M)
PrivilegesRequiredOverrideCurrentUserRecommended=僅為我安裝 (建議選項) (&M)


; *** Misc. errors
ErrorCreatingDir=安裝程式無法建立資料夾「%1」
ErrorTooManyFilesInDir=無法在資料夾「%1」內建立檔案，因為資料夾內有太多的檔案。


; *** Setup common messages
ExitSetupTitle=結束安裝程式
ExitSetupMessage=安裝尚未完成。如果您現在結束安裝程式，這個程式將不會被安裝。%n%n您可以稍後再執行安裝程式以完成安裝。%n%n您現在要結束安裝程式嗎？
AboutSetupMenuItem=關於安裝程式 (&A)...
AboutSetupTitle=關於安裝程式
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 網址：%n%4
AboutSetupNote=
TranslatorNote=


; *** Buttons
ButtonBack=< 上一步 (&B)
ButtonNext=下一步 (&N) >
ButtonInstall=安裝 (&I)
ButtonOK=確定
ButtonCancel=取消
ButtonYes=是 (&Y)
ButtonYesToAll=全部皆是 (&A)
ButtonNo=否 (&N)
ButtonNoToAll=全部皆否 (&O)
ButtonFinish=完成 (&F)
ButtonBrowse=瀏覽 (&B)...
ButtonWizardBrowse=瀏覽 (&R)...
ButtonNewFolder=建立新資料夾 (&M)


; *** "Select Language" dialog messages
SelectLanguageTitle=選擇安裝語言
SelectLanguageLabel=選擇在安裝過程中使用的語言：


; *** Common wizard text
ClickNext=按 「下一步」 繼續，或按 「取消」 結束安裝程式。
BeveledLabel=
BrowseDialogTitle=瀏覽資料夾
BrowseDialogLabel=在下面的資料夾清單中選擇一個資料夾，然後按 「確定」。
NewFolderName=新資料夾
