using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Machitan.Native;

internal static class NativeMethods
{
    // ---------- Window styles ----------
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_MINIMIZE = 0x20000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_MAXIMIZE = 0x01000000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_GROUP = 0x00020000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;

    public const uint BS_PUSHBUTTON = 0x00000000;
    public const uint BS_DEFPUSHBUTTON = 0x00000001;
    public const uint BS_CHECKBOX = 0x00000002;
    public const uint BS_AUTOCHECKBOX = 0x00000003;
    public const uint BS_RADIOBUTTON = 0x00000004;
    public const uint BS_3STATE = 0x00000005;
    public const uint BS_AUTO3STATE = 0x00000006;
    public const uint BS_GROUPBOX = 0x00000007;
    public const uint BS_USERBUTTON = 0x00000008;
    public const uint BS_AUTORADIOBUTTON = 0x00000009;
    public const uint BS_PUSHBOX = 0x0000000A;
    public const uint BS_OWNERDRAW = 0x0000000B;
    public const uint BS_TYPEMASK = 0x0000000F;
    public const uint BS_LEFTTEXT = 0x00000020;
    public const uint BS_TEXT = 0x00000000;
    public const uint BS_ICON = 0x00000040;
    public const uint BS_BITMAP = 0x00000080;
    public const uint BS_LEFT = 0x00000100;
    public const uint BS_RIGHT = 0x00000200;
    public const uint BS_CENTER = 0x00000300;
    public const uint BS_TOP = 0x00000400;
    public const uint BS_BOTTOM = 0x00000800;
    public const uint BS_VCENTER = 0x00000C00;
    public const uint BS_PUSHLIKE = 0x00001000;
    public const uint BS_MULTILINE = 0x00002000;
    public const uint BS_NOTIFY = 0x00004000;
    public const uint BS_FLAT = 0x00008000;
    public const uint BS_RIGHTBUTTON = 0x00000020;

    // ---------- Extended window styles ----------
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;
    public const uint WS_EX_NOPARENTNOTIFY = 0x00000004;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_ACCEPTFILES = 0x00000010;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_MDICHILD = 0x00000040;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_WINDOWEDGE = 0x00000100;
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_CONTEXTHELP = 0x00000400;
    public const uint WS_EX_RIGHT = 0x00001000;
    public const uint WS_EX_LEFT = 0x00000000;
    public const uint WS_EX_RTLREADING = 0x00002000;
    public const uint WS_EX_LEFTSCROLLBAR = 0x00004000;
    public const uint WS_EX_CONTROLPARENT = 0x00010000;
    public const uint WS_EX_STATICEDGE = 0x00020000;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WS_EX_COMPOSITED = 0x02000000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_TRANSPARENT_LAYERED = 0x00000020 | 0x00080000;

    // ---------- Layered window ----------
    public const uint LWA_ALPHA = 0x00000002;
    public const uint LWA_COLORKEY = 0x00000001;
    public const uint ULW_ALPHA = 0x00000002;

    // ---------- ShowWindow ----------
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWDEFAULT = 10;
    public const int SW_MINIMIZE = 6;
    public const int SW_FORCEMINIMIZE = 11;

    // ---------- Window messages ----------
    public const uint WM_NULL = 0x0000;
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOVE = 0x0003;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_ENABLE = 0x000A;
    public const uint WM_SETREDRAW = 0x000B;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SYSCOLORCHANGE = 0x0015;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_SHOWWINDOW = 0x0018;
    public const uint WM_CTLCOLOR = 0x0019;
    public const uint WM_WININICHANGE = 0x001A;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_CHILDACTIVATE = 0x0022;
    public const uint WM_QUEUESYNC = 0x0023;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_PAINTICON = 0x0026;
    public const uint WM_ICONERASEBKGND = 0x0027;
    public const uint WM_NEXTDLGCTL = 0x0028;
    public const uint WM_SPOOLERSTATUS = 0x002A;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_MEASUREITEM = 0x002C;
    public const uint WM_DELETEITEM = 0x002D;
    public const uint WM_VKEYTOITEM = 0x002E;
    public const uint WM_CHARTOITEM = 0x002F;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_GETFONT = 0x0031;
    public const uint WM_SETHOTKEY = 0x0032;
    public const uint WM_GETHOTKEY = 0x0033;
    public const uint WM_QUERYDRAGICON = 0x0037;
    public const uint WM_COMPAREITEM = 0x0039;
    public const uint WM_GETOBJECT = 0x003D;
    public const uint WM_COMPACTING = 0x0041;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;
    public const uint WM_POWER = 0x0048;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCPAINT = 0x0085;
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_GETDLGCODE = 0x0087;
    public const uint WM_NCMOUSEMOVE = 0x00A0;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_NCLBUTTONUP = 0x00A2;
    public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    public const uint WM_NCRBUTTONDOWN = 0x00A4;
    public const uint WM_NCRBUTTONUP = 0x00A5;
    public const uint WM_NCRBUTTONDBLCLK = 0x00A6;
    public const uint WM_NCMBUTTONDOWN = 0x00A7;
    public const uint WM_NCMBUTTONUP = 0x00A8;
    public const uint WM_NCMBUTTONDBLCLK = 0x00A9;
    public const uint WM_KEYFIRST = 0x0100;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_DEADCHAR = 0x0103;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_SYSDEADCHAR = 0x0107;
    public const uint WM_KEYLAST = 0x0108;
    public const uint WM_INITDIALOG = 0x0110;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_HSCROLL = 0x0114;
    public const uint WM_VSCROLL = 0x0115;
    public const uint WM_INITMENU = 0x0116;
    public const uint WM_INITMENUPOPUP = 0x0117;
    public const uint WM_SYSTIMER = 0x0118;
    public const uint WM_MENUSELECT = 0x011F;
    public const uint WM_MENUCHAR = 0x0120;
    public const uint WM_ENTERIDLE = 0x0121;
    public const uint WM_MENURBUTTONUP = 0x0122;
    public const uint WM_MENUDRAG = 0x0123;
    public const uint WM_MENUGETOBJECT = 0x0124;
    public const uint WM_UNINITMENUPOPUP = 0x0125;
    public const uint WM_MENUCOMMAND = 0x0126;
    public const uint WM_CTLCOLORMSGBOX = 0x0132;
    public const uint WM_CTLCOLEDIT = 0x0133;
    public const uint WM_CTLCOLORLISTBOX = 0x0134;
    public const uint WM_CTLCOLORBTN = 0x0135;
    public const uint WM_CTLCOLORDLG = 0x0136;
    public const uint WM_CTLCOLORSCROLLBAR = 0x0137;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint WM_MOUSEFIRST = 0x0200;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_XBUTTONDBLCLK = 0x020D;
    public const uint WM_MOUSELAST = 0x020D;
    public const uint WM_PARENTNOTIFY = 0x0210;
    public const uint WM_ENTERMENULOOP = 0x0211;
    public const uint WM_EXITMENULOOP = 0x0212;
    public const uint WM_NEXTMENU = 0x0213;
    public const uint WM_SIZING = 0x0214;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_MOVING = 0x0216;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_DEVICECHANGE = 0x0219;
    public const uint WM_MDICREATE = 0x0220;
    public const uint WM_MDIDESTROY = 0x0221;
    public const uint WM_MDIACTIVATE = 0x0222;
    public const uint WM_MDIRESTORE = 0x0223;
    public const uint WM_MDINEXT = 0x0224;
    public const uint WM_MDIMAXIMIZE = 0x0225;
    public const uint WM_MDITILE = 0x0226;
    public const uint WM_MDICASCADE = 0x0227;
    public const uint WM_MDIICONARRANGE = 0x0228;
    public const uint WM_MDIGETACTIVE = 0x0229;
    public const uint WM_MDISETMENU = 0x0230;
    public const uint WM_DROPFILES = 0x0233;
    public const uint WM_TOUCH = 0x0240;
    public const uint WM_POINTERDEVICECHANGE = 0x0238;
    public const uint WM_POINTERDEVICEINRANGE = 0x0239;
    public const uint WM_POINTERDEVICEOUTOFRANGE = 0x023A;
    public const uint WM_TOUCHHITTESTING = 0x0240;
    public const uint WM_POINTERUPDATE = 0x0245;
    public const uint WM_POINTERDOWN = 0x0246;
    public const uint WM_POINTERUP = 0x0247;
    public const uint WM_POINTERENTER = 0x0248;
    public const uint WM_POINTERLEAVE = 0x0249;
    public const uint WM_POINTERROUTEENTER = 0x024A;
    public const uint WM_POINTERROUTELEAVE = 0x024B;
    public const uint WM_POINTERROUTERELEASED = 0x024C;
    public const uint WM_NCPOINTERUPDATE = 0x024D;
    public const uint WM_NCPOINTERDOWN = 0x024E;
    public const uint WM_NCPOINTERUP = 0x024F;
    public const uint WM_NCPOINTERENTER = 0x0250;
    public const uint WM_NCPOINTERLEAVE = 0x0251;
    public const uint WM_NCPOINTERROUTEENTER = 0x0252;
    public const uint WM_NCPOINTERROUTELEAVE = 0x0253;
    public const uint WM_NCPOINTERROUTERELEASED = 0x0254;
    public const uint WM_DPICHANGED = 0x02E0;

    public const uint WM_APP = 0x8000;
    public const uint WM_USER = 0x0400;

    // ---------- AppBar ----------
    public const uint ABM_NEW = 0;
    public const uint ABM_REMOVE = 1;
    public const uint ABM_QUERYPOS = 2;
    public const uint ABM_SETPOS = 3;
    public const uint ABM_GETSTATE = 4;
    public const uint ABM_GETTASKBARPOS = 5;
    public const uint ABM_ACTIVATE = 6;
    public const uint ABM_GETAUTOHIDEBAR = 7;
    public const uint ABM_SETAUTOHIDEBAR = 8;
    public const uint ABM_WINDOWPOSCHANGED = 9;
    public const uint ABM_SETSTATE = 10;

    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    public const int ABN_POSCHANGED = 1;
    public const int ABN_STATECHANGE = 2;
    public const int ABN_FULLSCREENAPP = 3;
    public const int ABN_WINDOWARRANGE = 4;

    // ---------- System metrics ----------
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CMONITORS = 80;
    public const int SM_CXPADDEDBORDER = 92;

    // ---------- DWM ----------
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    public const int DWMSBT_MAINWINDOW = 2;
    public const int DWMSBT_NONE = 0;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    // ---------- Window long / ptr ----------
    public const int GWL_WNDPROC = -4;
    public const int GWL_HINSTANCE = -6;
    public const int GWL_HWNDPARENT = -8;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWL_USERDATA = -21;
    public const int GWL_ID = -12;

    public const int GWLP_WNDPROC = -4;
    public const int GWLP_HINSTANCE = -6;
    public const int GWLP_HWNDPARENT = -8;
    public const int GWLP_USERDATA = -21;
    public const int GWLP_ID = -12;

    // ---------- Window class ----------
    public const int CS_VREDRAW = 0x0001;
    public const int CS_HREDRAW = 0x0002;
    public const int CS_DBLCLKS = 0x0008;
    public const int CS_OWNDC = 0x0020;
    public const int CS_CLASSDC = 0x0040;
    public const int CS_PARENTDC = 0x0080;
    public const int CS_NOCLOSE = 0x0200;
    public const int CS_SAVEBITS = 0x0800;
    public const int CS_BYTEALIGNCLIENT = 0x1000;
    public const int CS_BYTEALIGNWINDOW = 0x2000;
    public const int CS_GLOBALCLASS = 0x4000;
    public const int CS_IME = 0x00010000;
    public const int CS_DROPSHADOW = 0x00020000;

    // ---------- Stock objects ----------
    public const int WHITE_BRUSH = 0;
    public const int LTGRAY_BRUSH = 1;
    public const int GRAY_BRUSH = 2;
    public const int DKGRAY_BRUSH = 3;
    public const int BLACK_BRUSH = 4;
    public const int NULL_BRUSH = 5;
    public const int HOLLOW_BRUSH = NULL_BRUSH;
    public const int WHITE_PEN = 6;
    public const int BLACK_PEN = 7;
    public const int NULL_PEN = 8;
    public const int OEM_FIXED_FONT = 10;
    public const int ANSI_FIXED_FONT = 11;
    public const int ANSI_VAR_FONT = 12;
    public const int SYSTEM_FONT = 13;
    public const int DEVICE_DEFAULT_FONT = 14;
    public const int DEFAULT_PALETTE = 15;
    public const int SYSTEM_FIXED_FONT = 16;

    // ---------- Virtual keys ----------
    public const byte VK_LBUTTON = 0x01;
    public const byte VK_RBUTTON = 0x02;
    public const byte VK_CANCEL = 0x03;
    public const byte VK_MBUTTON = 0x04;
    public const byte VK_XBUTTON1 = 0x05;
    public const byte VK_XBUTTON2 = 0x06;
    public const byte VK_BACK = 0x08;
    public const byte VK_TAB = 0x09;
    public const byte VK_CLEAR = 0x0C;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const byte VK_PAUSE = 0x13;
    public const byte VK_CAPITAL = 0x14;
    public const byte VK_KANA = 0x15;
    public const byte VK_HANGEUL = 0x15;
    public const byte VK_HANGUL = 0x15;
    public const byte VK_JUNJA = 0x17;
    public const byte VK_FINAL = 0x18;
    public const byte VK_HANJA = 0x19;
    public const byte VK_KANJI = 0x19;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_CONVERT = 0x1C;
    public const byte VK_NONCONVERT = 0x1D;
    public const byte VK_ACCEPT = 0x1E;
    public const byte VK_MODECHANGE = 0x1F;
    public const byte VK_SPACE = 0x20;
    public const byte VK_PRIOR = 0x21;
    public const byte VK_NEXT = 0x22;
    public const byte VK_END = 0x23;
    public const byte VK_HOME = 0x24;
    public const byte VK_LEFT = 0x25;
    public const byte VK_UP = 0x26;
    public const byte VK_RIGHT = 0x27;
    public const byte VK_DOWN = 0x28;
    public const byte VK_SELECT = 0x29;
    public const byte VK_PRINT = 0x2A;
    public const byte VK_EXECUTE = 0x2B;
    public const byte VK_SNAPSHOT = 0x2C;
    public const byte VK_INSERT = 0x2D;
    public const byte VK_DELETE = 0x2E;
    public const byte VK_HELP = 0x2F;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_RWIN = 0x5C;
    public const byte VK_APPS = 0x5D;
    public const byte VK_SLEEP = 0x5F;
    public const byte VK_NUMPAD0 = 0x60;
    public const byte VK_NUMPAD1 = 0x61;
    public const byte VK_NUMPAD2 = 0x62;
    public const byte VK_NUMPAD3 = 0x63;
    public const byte VK_NUMPAD4 = 0x64;
    public const byte VK_NUMPAD5 = 0x65;
    public const byte VK_NUMPAD6 = 0x66;
    public const byte VK_NUMPAD7 = 0x67;
    public const byte VK_NUMPAD8 = 0x68;
    public const byte VK_NUMPAD9 = 0x69;
    public const byte VK_MULTIPLY = 0x6A;
    public const byte VK_ADD = 0x6B;
    public const byte VK_SEPARATOR = 0x6C;
    public const byte VK_SUBTRACT = 0x6D;
    public const byte VK_DECIMAL = 0x6E;
    public const byte VK_DIVIDE = 0x6F;
    public const byte VK_F1 = 0x70;
    public const byte VK_F2 = 0x71;
    public const byte VK_F3 = 0x72;
    public const byte VK_F4 = 0x73;
    public const byte VK_F5 = 0x74;
    public const byte VK_F6 = 0x75;
    public const byte VK_F7 = 0x76;
    public const byte VK_F8 = 0x77;
    public const byte VK_F9 = 0x78;
    public const byte VK_F10 = 0x79;
    public const byte VK_F11 = 0x7A;
    public const byte VK_F12 = 0x7B;
    public const byte VK_F13 = 0x7C;
    public const byte VK_F14 = 0x7D;
    public const byte VK_F15 = 0x7E;
    public const byte VK_F16 = 0x7F;
    public const byte VK_F17 = 0x80;
    public const byte VK_F18 = 0x81;
    public const byte VK_F19 = 0x82;
    public const byte VK_F20 = 0x83;
    public const byte VK_F21 = 0x84;
    public const byte VK_F22 = 0x85;
    public const byte VK_F23 = 0x86;
    public const byte VK_F24 = 0x87;
    public const byte VK_NUMLOCK = 0x90;
    public const byte VK_SCROLL = 0x91;
    public const byte VK_OEM_NEC_EQUAL = 0x92;
    public const byte VK_OEM_FJ_JISHO = 0x92;
    public const byte VK_OEM_FJ_MASSHOU = 0x93;
    public const byte VK_OEM_FJ_TOUROKU = 0x94;
    public const byte VK_OEM_FJ_LOYA = 0x95;
    public const byte VK_OEM_FJ_ROYA = 0x96;
    public const byte VK_LSHIFT = 0xA0;
    public const byte VK_RSHIFT = 0xA1;
    public const byte VK_LCONTROL = 0xA2;
    public const byte VK_RCONTROL = 0xA3;
    public const byte VK_LMENU = 0xA4;
    public const byte VK_RMENU = 0xA5;
    public const byte VK_BROWSER_BACK = 0xA6;
    public const byte VK_BROWSER_FORWARD = 0xA7;
    public const byte VK_BROWSER_REFRESH = 0xA8;
    public const byte VK_BROWSER_STOP = 0xA9;
    public const byte VK_BROWSER_SEARCH = 0xAA;
    public const byte VK_BROWSER_FAVORITES = 0xAB;
    public const byte VK_BROWSER_HOME = 0xAC;
    public const byte VK_VOLUME_MUTE = 0xAD;
    public const byte VK_VOLUME_DOWN = 0xAE;
    public const byte VK_VOLUME_UP = 0xAF;
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const byte VK_MEDIA_STOP = 0xB2;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const byte VK_LAUNCH_MAIL = 0xB4;
    public const byte VK_LAUNCH_MEDIA_SELECT = 0xB5;
    public const byte VK_LAUNCH_APP1 = 0xB6;
    public const byte VK_LAUNCH_APP2 = 0xB7;
    public const byte VK_OEM_1 = 0xBA;
    public const byte VK_OEM_PLUS = 0xBB;
    public const byte VK_OEM_COMMA = 0xBC;
    public const byte VK_OEM_MINUS = 0xBD;
    public const byte VK_OEM_PERIOD = 0xBE;
    public const byte VK_OEM_2 = 0xBF;
    public const byte VK_OEM_3 = 0xC0;
    public const byte VK_OEM_4 = 0xDB;
    public const byte VK_OEM_5 = 0xDC;
    public const byte VK_OEM_6 = 0xDD;
    public const byte VK_OEM_7 = 0xDE;
    public const byte VK_OEM_8 = 0xDF;
    public const byte VK_OEM_AX = 0xE1;
    public const byte VK_OEM_102 = 0xE2;
    public const byte VK_ICO_HELP = 0xE3;
    public const byte VK_ICO_00 = 0xE4;
    public const byte VK_PROCESSKEY = 0xE5;
    public const byte VK_ICO_CLEAR = 0xE6;
    public const byte VK_PACKET = 0xE7;
    public const byte VK_OEM_RESET = 0xE9;
    public const byte VK_OEM_JUMP = 0xEA;
    public const byte VK_OEM_PA1 = 0xEB;
    public const byte VK_OEM_PA2 = 0xEC;
    public const byte VK_OEM_PA3 = 0xED;
    public const byte VK_OEM_WSCTRL = 0xEE;
    public const byte VK_OEM_CUSEL = 0xEF;
    public const byte VK_OEM_ATTN = 0xF0;
    public const byte VK_OEM_FINISH = 0xF1;
    public const byte VK_OEM_COPY = 0xF2;
    public const byte VK_OEM_AUTO = 0xF3;
    public const byte VK_OEM_ENLW = 0xF4;
    public const byte VK_OEM_BACKTAB = 0xF5;
    public const byte VK_ATTN = 0xF6;
    public const byte VK_CRSEL = 0xF7;
    public const byte VK_EXSEL = 0xF8;
    public const byte VK_EREOF = 0xF9;
    public const byte VK_PLAY = 0xFA;
    public const byte VK_ZOOM = 0xFB;
    public const byte VK_NONAME = 0xFC;
    public const byte VK_PA1 = 0xFD;
    public const byte VK_OEM_CLEAR = 0xFE;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    // ---------- SWP flags ----------
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_DRAWFRAME = SWP_FRAMECHANGED;
    public const uint SWP_NOREPOSITION = SWP_NOOWNERZORDER;
    public const uint SWP_DEFERERASE = 0x2000;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // ---------- HT values ----------
    public const int HTERROR = -2;
    public const int HTTRANSPARENT = -1;
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTSYSMENU = 3;
    public const int HTGROWBOX = 4;
    public const int HTSIZE = HTGROWBOX;
    public const int HTMENU = 5;
    public const int HTHSCROLL = 6;
    public const int HTVSCROLL = 7;
    public const int HTMINBUTTON = 8;
    public const int HTMAXBUTTON = 9;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTBORDER = 18;
    public const int HTREDUCE = HTMINBUTTON;
    public const int HTZOOM = HTMAXBUTTON;
    public const int HTSIZEFIRST = HTLEFT;
    public const int HTSIZELAST = HTBOTTOMRIGHT;
    public const int HTOBJECT = 19;
    public const int HTCLOSE = 20;
    public const int HTHELP = 21;

    // ---------- GDI ----------
    public const int SRCCOPY = 0x00CC0020;
    public const int SRCPAINT = 0x00EE0086;
    public const int SRCAND = 0x008800C6;
    public const int SRCINVERT = 0x00660046;
    public const int SRCERASE = 0x00440328;
    public const int NOTSRCCOPY = 0x00330008;
    public const int NOTSRCERASE = 0x001100A6;
    public const int MERGECOPY = 0x00C000CA;
    public const int MERGEPAINT = 0x00BB0226;
    public const int PATCOPY = 0x00F00021;
    public const int PATPAINT = 0x00FB0A09;
    public const int PATINVERT = 0x005A0049;
    public const int DSTINVERT = 0x00550009;
    public const int BLACKNESS = 0x00000042;
    public const int WHITENESS = 0x00FF0062;

    public const int PS_SOLID = 0;
    public const int PS_DASH = 1;
    public const int PS_DOT = 2;
    public const int PS_DASHDOT = 3;
    public const int PS_DASHDOTDOT = 4;
    public const int PS_NULL = 5;
    public const int PS_INSIDEFRAME = 6;
    public const int PS_USERSTYLE = 7;
    public const int PS_ALTERNATE = 8;
    public const int PS_STYLE_MASK = 0x0000000F;

    public const int BI_RGB = 0;
    public const int BI_RLE8 = 1;
    public const int BI_RLE4 = 2;
    public const int BI_BITFIELDS = 3;
    public const int BI_JPEG = 4;
    public const int BI_PNG = 5;

    public const int DIB_RGB_COLORS = 0;
    public const int DIB_PAL_COLORS = 1;

    public const int GDIPLUS_OK = 0;

    // ---------- GetWindow / GW ----------
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;
    public const uint GW_ENABLEDPOPUP = 6;

    // ---------- COM ----------
    public const int CLSCTX_INPROC_SERVER = 0x1;
    public const int CLSCTX_INPROC_HANDLER = 0x2;
    public const int CLSCTX_LOCAL_SERVER = 0x4;
    public const int S_OK = 0;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);

    // ---------- Shell / Tray ----------
    // NotifyIcon messages
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETFOCUS = 0x00000003;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_GUID = 0x00000020;
    public const uint NIF_REALTIME = 0x00000040;
    public const uint NIF_SHOWTIP = 0x00000080;

    public const uint NIIF_NONE = 0x00000000;
    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;
    public const uint NIIF_ERROR = 0x00000003;
    public const uint NIIF_NOSOUND = 0x00000010;
    public const uint NIIF_LARGE_ICON = 0x00000020;
    public const uint NIIF_RESPECT_QUIET_TIME = 0x00000080;

    // ITrayNotify GUIDs (from Windows SDK)
    public static readonly Guid CLSID_TrayNotify = new("{b5e3f921-7b84-426f-b3e4-3a2c1f4c0f11}"); // placeholder
    public static readonly Guid IID_ITrayNotify = new("{a6d5c4b1-2f3d-4e5a-8b7c-9d0e1f2a3b4c}"); // placeholder

    // ---------- Shell links ----------
    public const uint SLGP_SHORTPATH = 0x1;
    public const uint SLGP_UNCPRIORITY = 0x2;
    public const uint SLGP_RAWPATH = 0x4;

    public const uint SLR_NO_UI = 0x1;
    public const uint SLR_ANY_MATCH = 0x2;
    public const uint SLR_UPDATE = 0x4;
    public const uint SLR_NOUPDATE = 0x8;
    public const uint SLR_NO_SEARCH = 0x10;
    public const uint SLR_NOTRACK = 0x20;
    public const uint SLR_NOLINKINFO = 0x40;
    public const uint SLR_INVOKE_MSI = 0x80;

    // ---------- DllImports ----------
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fUnknown);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    public static extern int FrameRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    public static extern bool InvertRect(IntPtr hDC, ref RECT lprc);

    [DllImport("user32.dll")]
    public static extern bool DrawEdge(IntPtr hdc, ref RECT qrc, int edge, int grfFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSysColorBrush(int nIndex);

    [DllImport("user32.dll")]
    public static extern uint GetSysColor(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    public static extern IntPtr RealChildWindowFromIntPtr(IntPtr hWndParent, IntPtr hwndChild);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_ROOT = 2;
    public const uint GA_PARENT = 1;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int iBkMode);

    public const int TRANSPARENT = 1;
    public const int OPAQUE = 2;

    [DllImport("gdi32.dll")]
    public static extern int SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll")]
    public static extern int SetBkColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll")]
    public static extern bool TextOut(IntPtr hdc, int nXStart, int nYStart, string lpString, int cbString);

    [DllImport("gdi32.dll")]
    public static extern bool ExtTextOut(IntPtr hdc, int X, int Y, uint fuOptions, ref RECT lprc, string lpString, uint cbString, IntPtr lpDx);

    [DllImport("gdi32.dll")]
    public static extern int GetTextMetrics(IntPtr hdc, out TEXTMETRIC lptm);

    [DllImport("gdi32.dll")]
    public static extern bool GetTextExtentPoint32(IntPtr hdc, string lpString, int cchString, out SIZE lpSize);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateFontIndirect(ref LOGFONT lplf);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreatePen(int fnStyle, int nWidth, uint crColor);

    [DllImport("shell32.dll")]
    public static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    public static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern int SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_DISPLAYNAME = 0x000000200;
    public const uint SHGFI_TYPENAME = 0x000000400;
    public const uint SHGFI_ATTRIBUTES = 0x000000800;
    public const uint SHGFI_ICONLOCATION = 0x000001000;
    public const uint SHGFI_EXETYPE = 0x000002000;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint SHGFI_LINKOVERLAY = 0x000008000;
    public const uint SHGFI_SELECTED = 0x000010000;
    public const uint SHGFI_ATTR_SPECIFIED = 0x000020000;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_OPENICON = 0x000000002;
    public const uint SHGFI_SHELLICONSIZE = 0x000000004;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_ADDOVERLAYS = 0x000000020;
    public const uint SHGFI_OVERLAYINDEX = 0x000000040;

    // ---------- COM ----------
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    public static extern int CoInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    // ---------- Structures ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc;
        public IntPtr lppos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRIC
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    public static int LOWORD(long value) => (int)(value & 0xFFFF);
    public static int HIWORD(long value) => (int)((value >> 16) & 0xFFFF);
}
