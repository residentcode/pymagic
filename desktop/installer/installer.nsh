# Pieces of PyMagic's setup wizard that electron-builder's NSIS template has no switch for (desktop/build-config.mjs hands this file over as nsis.include):
#  - a page with a check box for the desktop shortcut (the template only knows "always" or "never"),
#  - a folder page that only takes a folder PyMagic can own: one that is not there yet, an empty one, or the folder of an earlier PyMagic.
#    The template keeps a chosen folder as it is when its path already contains "PyMagic", and uninstalling or updating deletes that folder whole,
#    so a folder with other files in it (a git checkout of PyMagic, say) must never be accepted.
# The functions sit inside customPageAfterChangeDir because this file is read before the template's own helpers (StdUtils and the like) exist.
!include nsDialogs.nsh

!ifndef BUILD_UNINSTALLER

Var PMDesktopBox
Var PMDesktopWanted

!macro customInit
  StrCpy $PMDesktopWanted ${BST_CHECKED}
  # a silent install (or /D=folder) never shows the folder page: the same rule is held here
  Call PMCheckFolder
  ${If} ${Silent}
    ${If} $0 == "no"
      SetErrorLevel 2
      Abort
    ${EndIf}
    # the wizard adds "\PyMagic" to a folder that does not name it when it leaves the folder page; a silent install has no such page
    StrCpy $INSTDIR "$1"
  ${EndIf}
!macroend

!macro customPageAfterChangeDir
  # the folder the install would really use: the template adds "\PyMagic" to a chosen path that does not contain the name
  # in: $INSTDIR   out: $0 = "ok" or "no", $1 = the folder that is checked
  Function PMCheckFolder
    Push $2
    Push $3
    Push $4
    Push $5
    # does the path contain the name ("PyMagic", in any case)?
    StrLen $4 "${APP_FILENAME}"
    StrCpy $5 0
    StrCpy $2 ""
    pmfind:
      StrCpy $3 "$INSTDIR" $4 $5
      StrCmp $3 "" pmfound
      StrCmp $3 "${APP_FILENAME}" 0 pmmore
      StrCpy $2 "yes"
      Goto pmfound
    pmmore:
      IntOp $5 $5 + 1
      Goto pmfind
    pmfound:
    StrCpy $1 "$INSTDIR"
    ${If} $2 == ""
      StrCpy $1 "$INSTDIR\${APP_FILENAME}"
    ${EndIf}
    StrCpy $0 "ok"
    IfFileExists "$1\*.*" 0 pmdone
    IfFileExists "$1\${APP_FILENAME}.exe" pmdone
    FindFirst $2 $3 "$1\*.*"
    pmloop:
      StrCmp $3 "" pmclose
      StrCmp $3 "." pmnext
      StrCmp $3 ".." pmnext
      StrCpy $0 "no"
      Goto pmclose
    pmnext:
      FindNext $2 $3
      Goto pmloop
    pmclose:
      FindClose $2
    pmdone:
    Pop $5
    Pop $4
    Pop $3
    Pop $2
  FunctionEnd

  # called by the folder page whenever the folder changes: "Next" stays off while it says no
  Function .onVerifyInstDir
    Call PMCheckFolder
    ${If} $0 == "no"
      Abort
    ${EndIf}
  FunctionEnd

  Function PMShortcutPage
    ${If} ${isUpdated}
      Abort
    ${EndIf}
    !insertmacro MUI_HEADER_TEXT "Shortcuts" "Choose what the setup puts on your computer besides the program."
    nsDialogs::Create 1018
    Pop $0
    ${NSD_CreateCheckbox} 0 0 100% 12u "Create a &desktop shortcut"
    Pop $PMDesktopBox
    ${NSD_SetState} $PMDesktopBox $PMDesktopWanted
    ${NSD_CreateLabel} 0 18u 100% 24u "The Start menu entry is always made. The shortcut on the desktop is up to you; PyMagic is also found by typing its name in the Start menu."
    Pop $0
    nsDialogs::Show
  FunctionEnd

  Function PMShortcutPageLeave
    ${NSD_GetState} $PMDesktopBox $PMDesktopWanted
  FunctionEnd

  Page custom PMShortcutPage PMShortcutPageLeave
!macroend

!macro customInstall
  ${If} $PMDesktopWanted == ${BST_CHECKED}
    CreateShortCut "$DESKTOP\${SHORTCUT_NAME}.lnk" "$appExe" "" "$appExe" 0 "" "" "${APP_DESCRIPTION}"
    ClearErrors
    WinShell::SetLnkAUMI "$DESKTOP\${SHORTCUT_NAME}.lnk" "${APP_ID}"
  ${EndIf}
!macroend

!else

# the shortcut is made by the macro above, not by the template, so the uninstaller takes it away here
!macro customUnInstall
  WinShell::UninstShortcut "$DESKTOP\${SHORTCUT_NAME}.lnk"
  Delete "$DESKTOP\${SHORTCUT_NAME}.lnk"
!macroend

!endif
