const std = @import("std");
const com = @import("../os/windows_com.zig");

pub const GUID = com.GUID;
pub const HRESULT = com.HRESULT;
pub const SUCCEEDED = com.SUCCEEDED;
pub const FAILED = com.FAILED;
pub const S_OK = com.S_OK;
pub const E_NOINTERFACE = com.E_NOINTERFACE;
pub const IUnknown = com.IUnknown;
pub const Reserved = com.Reserved;

const BOOL = i32;
const WCHAR = u16;
const UINT32 = u32;
const UINT16 = u16;
const FLOAT = f32;

// --- Enums ---

pub const DWRITE_FACTORY_TYPE = enum(u32) {
    SHARED = 0,
    ISOLATED = 1,
};

pub const DWRITE_FONT_WEIGHT = enum(u32) {
    THIN = 100,
    EXTRA_LIGHT = 200,
    LIGHT = 300,
    SEMI_LIGHT = 350,
    NORMAL = 400,
    MEDIUM = 500,
    SEMI_BOLD = 600,
    BOLD = 700,
    EXTRA_BOLD = 800,
    BLACK = 900,
    EXTRA_BLACK = 950,
    _,
};

pub const DWRITE_FONT_STYLE = enum(u32) {
    NORMAL = 0,
    OBLIQUE = 1,
    ITALIC = 2,
};

pub const DWRITE_FONT_STRETCH = enum(u32) {
    UNDEFINED = 0,
    ULTRA_CONDENSED = 1,
    EXTRA_CONDENSED = 2,
    CONDENSED = 3,
    SEMI_CONDENSED = 4,
    NORMAL = 5,
    SEMI_EXPANDED = 6,
    EXPANDED = 7,
    EXTRA_EXPANDED = 8,
    ULTRA_EXPANDED = 9,
};

pub const DWRITE_FONT_SIMULATIONS = enum(u32) {
    NONE = 0,
    BOLD = 1,
    OBLIQUE = 2,
    _,
};

pub const DWRITE_INFORMATIONAL_STRING_ID = enum(u32) {
    NONE = 0,
    COPYRIGHT_NOTICE = 1,
    VERSION_STRINGS = 2,
    TRADEMARK = 3,
    MANUFACTURER = 4,
    DESIGNER = 5,
    DESIGNER_URL = 6,
    DESCRIPTION = 7,
    FONT_VENDOR_URL = 8,
    LICENSE_DESCRIPTION = 9,
    LICENSE_INFO_URL = 10,
    WIN32_FAMILY_NAMES = 11,
    WIN32_SUBFAMILY_NAMES = 12,
    TYPOGRAPHIC_FAMILY_NAMES = 13,
    TYPOGRAPHIC_SUBFAMILY_NAMES = 14,
    SAMPLE_TEXT = 15,
    FULL_NAME = 16,
    POSTSCRIPT_NAME = 17,
    POSTSCRIPT_CID_NAME = 18,
};

pub const DWRITE_READING_DIRECTION = enum(u32) {
    LEFT_TO_RIGHT = 0,
    RIGHT_TO_LEFT = 1,
};

// --- Structs ---

pub const DWRITE_UNICODE_RANGE = extern struct {
    first: UINT32,
    last: UINT32,
};

// --- COM Interfaces ---

// IDWriteNumberSubstitution -- IUnknown only, no extra methods.
pub const IDWriteNumberSubstitution = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDWriteNumberSubstitution, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteNumberSubstitution) callconv(.winapi) u32,
        Release: *const fn (*IDWriteNumberSubstitution) callconv(.winapi) u32,
    };

    pub inline fn Release(self: *IDWriteNumberSubstitution) u32 {
        return self.vtable.Release(self);
    }
};

// IDWriteLocalizedStrings
// Slots: GetCount(3), FindLocaleName(4), [5-6 Reserved], GetStringLength(7), GetString(8)
pub const IDWriteLocalizedStrings = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteLocalizedStrings) callconv(.winapi) u32,
        // IDWriteLocalizedStrings (slots 3-8)
        GetCount: *const fn (*IDWriteLocalizedStrings) callconv(.winapi) UINT32,
        FindLocaleName: *const fn (
            *IDWriteLocalizedStrings,
            localeName: [*:0]const WCHAR,
            index: *UINT32,
            exists: *BOOL,
        ) callconv(.winapi) HRESULT,
        GetLocaleNameLength: Reserved,
        GetLocaleName: Reserved,
        GetStringLength: *const fn (
            *IDWriteLocalizedStrings,
            index: UINT32,
            length: *UINT32,
        ) callconv(.winapi) HRESULT,
        GetString: *const fn (
            *IDWriteLocalizedStrings,
            index: UINT32,
            stringBuffer: [*]WCHAR,
            size: UINT32,
        ) callconv(.winapi) HRESULT,
    };

    pub inline fn Release(self: *IDWriteLocalizedStrings) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetCount(self: *IDWriteLocalizedStrings) UINT32 {
        return self.vtable.GetCount(self);
    }

    pub inline fn FindLocaleName(
        self: *IDWriteLocalizedStrings,
        localeName: [*:0]const WCHAR,
        index: *UINT32,
        exists: *BOOL,
    ) HRESULT {
        return self.vtable.FindLocaleName(self, localeName, index, exists);
    }

    pub inline fn GetStringLength(self: *IDWriteLocalizedStrings, index: UINT32, length: *UINT32) HRESULT {
        return self.vtable.GetStringLength(self, index, length);
    }

    pub inline fn GetString(self: *IDWriteLocalizedStrings, index: UINT32, stringBuffer: [*]WCHAR, size: UINT32) HRESULT {
        return self.vtable.GetString(self, index, stringBuffer, size);
    }
};

// IDWriteFontFace
// Slots: [3 Reserved], GetFiles(4), GetIndex(5), [6-17 Reserved]
pub const IDWriteFontFace = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFace) callconv(.winapi) u32,
        // IDWriteFontFace (slots 3-17)
        GetType: Reserved,
        GetFiles: *const fn (
            *IDWriteFontFace,
            numberOfFiles: *UINT32,
            fontFiles: ?[*]?*IDWriteFontFile,
        ) callconv(.winapi) HRESULT,
        GetIndex: *const fn (*IDWriteFontFace) callconv(.winapi) UINT32,
        GetSimulations: Reserved,
        IsSymbolFont: Reserved,
        GetMetrics: Reserved,
        GetGlyphCount: Reserved,
        GetDesignGlyphMetrics: Reserved,
        GetGlyphIndices: Reserved,
        TryGetFontTable: Reserved,
        ReleaseFontTable: Reserved,
        GetGlyphRunOutline: Reserved,
        GetRecommendedRenderingMode: Reserved,
        GetGdiCompatibleMetrics: Reserved,
        GetGdiCompatibleGlyphMetrics: Reserved,
    };

    pub inline fn Release(self: *IDWriteFontFace) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFiles(
        self: *IDWriteFontFace,
        numberOfFiles: *UINT32,
        fontFiles: ?[*]?*IDWriteFontFile,
    ) HRESULT {
        return self.vtable.GetFiles(self, numberOfFiles, fontFiles);
    }

    pub inline fn GetIndex(self: *IDWriteFontFace) UINT32 {
        return self.vtable.GetIndex(self);
    }
};

// IDWriteFontFileLoader (IID needed to QI to IDWriteLocalFontFileLoader)
pub const IDWriteFontFileLoader = extern struct {
    vtable: *const VTable,

    pub const IID = GUID{
        .data1 = 0x727cad4e,
        .data2 = 0xd6af,
        .data3 = 0x4c9e,
        .data4 = .{ 0x8a, 0x08, 0xd6, 0x95, 0xb1, 0x1c, 0xaa, 0x49 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDWriteFontFileLoader, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteFontFileLoader) callconv(.winapi) u32,
        Release: *const fn (*IDWriteFontFileLoader) callconv(.winapi) u32,
        // IDWriteFontFileLoader (slot 3)
        CreateStreamFromKey: Reserved,
    };

    pub inline fn QueryInterface(self: *IDWriteFontFileLoader, riid: *const GUID, ppv: *?*anyopaque) HRESULT {
        return self.vtable.QueryInterface(self, riid, ppv);
    }

    pub inline fn Release(self: *IDWriteFontFileLoader) u32 {
        return self.vtable.Release(self);
    }
};

// IDWriteLocalFontFileLoader
pub const IDWriteLocalFontFileLoader = extern struct {
    vtable: *const VTable,

    pub const IID = GUID{
        .data1 = 0xb2d9f3ec,
        .data2 = 0xc9fe,
        .data3 = 0x4a11,
        .data4 = .{ 0xa2, 0xec, 0xd8, 0x62, 0x08, 0xf7, 0xc0, 0xa2 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteLocalFontFileLoader) callconv(.winapi) u32,
        // IDWriteFontFileLoader (slot 3)
        CreateStreamFromKey: Reserved,
        // IDWriteLocalFontFileLoader (slots 4-6)
        GetFilePathLengthFromKey: *const fn (
            *IDWriteLocalFontFileLoader,
            fontFileReferenceKey: *const anyopaque,
            fontFileReferenceKeySize: UINT32,
            filePathLength: *UINT32,
        ) callconv(.winapi) HRESULT,
        GetFilePathFromKey: *const fn (
            *IDWriteLocalFontFileLoader,
            fontFileReferenceKey: *const anyopaque,
            fontFileReferenceKeySize: UINT32,
            filePath: [*]WCHAR,
            filePathSize: UINT32,
        ) callconv(.winapi) HRESULT,
        GetLastWriteTimeFromKey: Reserved,
    };

    pub inline fn Release(self: *IDWriteLocalFontFileLoader) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFilePathLengthFromKey(
        self: *IDWriteLocalFontFileLoader,
        key: *const anyopaque,
        key_size: UINT32,
        path_len: *UINT32,
    ) HRESULT {
        return self.vtable.GetFilePathLengthFromKey(self, key, key_size, path_len);
    }

    pub inline fn GetFilePathFromKey(
        self: *IDWriteLocalFontFileLoader,
        key: *const anyopaque,
        key_size: UINT32,
        path: [*]WCHAR,
        path_size: UINT32,
    ) HRESULT {
        return self.vtable.GetFilePathFromKey(self, key, key_size, path, path_size);
    }
};

// IDWriteFontFile
// Slots: GetReferenceKey(3), GetLoader(4), [5 Reserved]
pub const IDWriteFontFile = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFile) callconv(.winapi) u32,
        // IDWriteFontFile (slots 3-5)
        GetReferenceKey: *const fn (
            *IDWriteFontFile,
            fontFileReferenceKey: *?*const anyopaque,
            fontFileReferenceKeySize: *UINT32,
        ) callconv(.winapi) HRESULT,
        GetLoader: *const fn (
            *IDWriteFontFile,
            fontFileLoader: *?*IDWriteFontFileLoader,
        ) callconv(.winapi) HRESULT,
        Analyze: Reserved,
    };

    pub inline fn Release(self: *IDWriteFontFile) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetReferenceKey(
        self: *IDWriteFontFile,
        key: *?*const anyopaque,
        key_size: *UINT32,
    ) HRESULT {
        return self.vtable.GetReferenceKey(self, key, key_size);
    }

    pub inline fn GetLoader(self: *IDWriteFontFile, loader: *?*IDWriteFontFileLoader) HRESULT {
        return self.vtable.GetLoader(self, loader);
    }
};

// IDWriteTextAnalysisSource -- callback interface we implement.
// DWrite calls our methods through this vtable.
pub const IDWriteTextAnalysisSource = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDWriteTextAnalysisSource, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteTextAnalysisSource) callconv(.winapi) u32,
        Release: *const fn (*IDWriteTextAnalysisSource) callconv(.winapi) u32,
        // IDWriteTextAnalysisSource (slots 3-7)
        GetTextAtPosition: *const fn (
            *IDWriteTextAnalysisSource,
            textPosition: UINT32,
            textString: *?[*]const WCHAR,
            textLength: *UINT32,
        ) callconv(.winapi) HRESULT,
        GetTextBeforePosition: *const fn (
            *IDWriteTextAnalysisSource,
            textPosition: UINT32,
            textString: *?[*]const WCHAR,
            textLength: *UINT32,
        ) callconv(.winapi) HRESULT,
        GetParagraphReadingDirection: *const fn (
            *IDWriteTextAnalysisSource,
        ) callconv(.winapi) DWRITE_READING_DIRECTION,
        GetLocaleName: *const fn (
            *IDWriteTextAnalysisSource,
            textPosition: UINT32,
            textLength: *UINT32,
            localeName: *?[*:0]const WCHAR,
        ) callconv(.winapi) HRESULT,
        GetNumberSubstitution: *const fn (
            *IDWriteTextAnalysisSource,
            textPosition: UINT32,
            textLength: *UINT32,
            numberSubstitution: *?*IDWriteNumberSubstitution,
        ) callconv(.winapi) HRESULT,
    };
};

// IDWriteFontFallback
// Slot 3: MapCharacters
pub const IDWriteFontFallback = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFallback) callconv(.winapi) u32,
        // IDWriteFontFallback (slot 3)
        MapCharacters: *const fn (
            *IDWriteFontFallback,
            analysisSource: *IDWriteTextAnalysisSource,
            textPosition: UINT32,
            textLength: UINT32,
            baseFontCollection: ?*IDWriteFontCollection,
            baseFamilyName: ?[*:0]const WCHAR,
            baseWeight: DWRITE_FONT_WEIGHT,
            baseStyle: DWRITE_FONT_STYLE,
            baseStretch: DWRITE_FONT_STRETCH,
            mappedLength: *UINT32,
            mappedFont: *?*IDWriteFont,
            scale: *FLOAT,
        ) callconv(.winapi) HRESULT,
    };

    pub inline fn Release(self: *IDWriteFontFallback) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn MapCharacters(
        self: *IDWriteFontFallback,
        analysisSource: *IDWriteTextAnalysisSource,
        textPosition: UINT32,
        textLength: UINT32,
        baseFontCollection: ?*IDWriteFontCollection,
        baseFamilyName: ?[*:0]const WCHAR,
        baseWeight: DWRITE_FONT_WEIGHT,
        baseStyle: DWRITE_FONT_STYLE,
        baseStretch: DWRITE_FONT_STRETCH,
        mappedLength: *UINT32,
        mappedFont: *?*IDWriteFont,
        scale: *FLOAT,
    ) HRESULT {
        return self.vtable.MapCharacters(
            self,
            analysisSource,
            textPosition,
            textLength,
            baseFontCollection,
            baseFamilyName,
            baseWeight,
            baseStyle,
            baseStretch,
            mappedLength,
            mappedFont,
            scale,
        );
    }
};

// IDWriteFont (through IDWriteFont2 for IsColorFont)
// Slots: GetFontFamily(3), GetWeight(4), GetStretch(5), GetStyle(6), IsSymbolFont(7),
//        GetFaceNames(8), GetInformationalStrings(9), GetSimulations(10),
//        GetMetrics(11), HasCharacter(12), CreateFontFace(13),
//        IDWriteFont1 slots 14-17, IsColorFont(18 IDWriteFont2)
pub const IDWriteFont = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: *const fn (*IDWriteFont) callconv(.winapi) u32,
        Release: *const fn (*IDWriteFont) callconv(.winapi) u32,
        // IDWriteFont (slots 3-13)
        GetFontFamily: Reserved,
        GetWeight: *const fn (*IDWriteFont) callconv(.winapi) DWRITE_FONT_WEIGHT,
        GetStretch: *const fn (*IDWriteFont) callconv(.winapi) DWRITE_FONT_STRETCH,
        GetStyle: *const fn (*IDWriteFont) callconv(.winapi) DWRITE_FONT_STYLE,
        IsSymbolFont: Reserved,
        GetFaceNames: *const fn (*IDWriteFont, names: *?*IDWriteLocalizedStrings) callconv(.winapi) HRESULT,
        GetInformationalStrings: *const fn (
            *IDWriteFont,
            informationalStringID: DWRITE_INFORMATIONAL_STRING_ID,
            informationalStrings: *?*IDWriteLocalizedStrings,
            exists: *BOOL,
        ) callconv(.winapi) HRESULT,
        GetSimulations: *const fn (*IDWriteFont) callconv(.winapi) DWRITE_FONT_SIMULATIONS,
        GetMetrics: Reserved,
        HasCharacter: *const fn (*IDWriteFont, unicodeValue: UINT32, exists: *BOOL) callconv(.winapi) HRESULT,
        CreateFontFace: *const fn (*IDWriteFont, fontFace: *?*IDWriteFontFace) callconv(.winapi) HRESULT,
        // IDWriteFont1 (slots 14-17)
        _slot14: Reserved,
        _slot15: Reserved,
        _slot16: Reserved,
        _slot17: Reserved,
        // IDWriteFont2 (slot 18)
        IsColorFont: *const fn (*IDWriteFont) callconv(.winapi) BOOL,
    };

    pub inline fn AddRef(self: *IDWriteFont) u32 {
        return self.vtable.AddRef(self);
    }

    pub inline fn Release(self: *IDWriteFont) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetWeight(self: *IDWriteFont) DWRITE_FONT_WEIGHT {
        return self.vtable.GetWeight(self);
    }

    pub inline fn GetStretch(self: *IDWriteFont) DWRITE_FONT_STRETCH {
        return self.vtable.GetStretch(self);
    }

    pub inline fn GetStyle(self: *IDWriteFont) DWRITE_FONT_STYLE {
        return self.vtable.GetStyle(self);
    }

    pub inline fn GetFaceNames(self: *IDWriteFont, names: *?*IDWriteLocalizedStrings) HRESULT {
        return self.vtable.GetFaceNames(self, names);
    }

    pub inline fn GetInformationalStrings(
        self: *IDWriteFont,
        id: DWRITE_INFORMATIONAL_STRING_ID,
        strings: *?*IDWriteLocalizedStrings,
        exists: *BOOL,
    ) HRESULT {
        return self.vtable.GetInformationalStrings(self, id, strings, exists);
    }

    pub inline fn GetSimulations(self: *IDWriteFont) DWRITE_FONT_SIMULATIONS {
        return self.vtable.GetSimulations(self);
    }

    pub inline fn HasCharacter(self: *IDWriteFont, unicodeValue: UINT32, exists: *BOOL) HRESULT {
        return self.vtable.HasCharacter(self, unicodeValue, exists);
    }

    pub inline fn CreateFontFace(self: *IDWriteFont, fontFace: *?*IDWriteFontFace) HRESULT {
        return self.vtable.CreateFontFace(self, fontFace);
    }

    pub inline fn IsColorFont(self: *IDWriteFont) BOOL {
        return self.vtable.IsColorFont(self);
    }
};

// IDWriteFontFamily (extends IDWriteFontList)
// IDWriteFontList slots 3-5: [3 Reserved], GetFontCount(4), GetFont(5)
// IDWriteFontFamily slots 6-7: GetFamilyNames(6), [7 Reserved]
pub const IDWriteFontFamily = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFamily) callconv(.winapi) u32,
        // IDWriteFontList (slots 3-5)
        GetFontCollection: Reserved,
        GetFontCount: *const fn (*IDWriteFontFamily) callconv(.winapi) UINT32,
        GetFont: *const fn (*IDWriteFontFamily, index: UINT32, font: *?*IDWriteFont) callconv(.winapi) HRESULT,
        // IDWriteFontFamily (slots 6-7)
        GetFamilyNames: *const fn (*IDWriteFontFamily, names: *?*IDWriteLocalizedStrings) callconv(.winapi) HRESULT,
        MatchClosestFont: Reserved,
    };

    pub inline fn Release(self: *IDWriteFontFamily) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFontCount(self: *IDWriteFontFamily) UINT32 {
        return self.vtable.GetFontCount(self);
    }

    pub inline fn GetFont(self: *IDWriteFontFamily, index: UINT32, font: *?*IDWriteFont) HRESULT {
        return self.vtable.GetFont(self, index, font);
    }

    pub inline fn GetFamilyNames(self: *IDWriteFontFamily, names: *?*IDWriteLocalizedStrings) HRESULT {
        return self.vtable.GetFamilyNames(self, names);
    }
};

// IDWriteFontCollection
// Slots: GetFontFamilyCount(3), GetFontFamily(4), FindFamilyName(5), GetFontFromFontFace(6)
pub const IDWriteFontCollection = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontCollection) callconv(.winapi) u32,
        // IDWriteFontCollection (slots 3-6)
        GetFontFamilyCount: *const fn (*IDWriteFontCollection) callconv(.winapi) UINT32,
        GetFontFamily: *const fn (
            *IDWriteFontCollection,
            index: UINT32,
            fontFamily: *?*IDWriteFontFamily,
        ) callconv(.winapi) HRESULT,
        FindFamilyName: *const fn (
            *IDWriteFontCollection,
            familyName: [*:0]const WCHAR,
            index: *UINT32,
            exists: *BOOL,
        ) callconv(.winapi) HRESULT,
        GetFontFromFontFace: Reserved,
    };

    pub inline fn Release(self: *IDWriteFontCollection) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFontFamilyCount(self: *IDWriteFontCollection) UINT32 {
        return self.vtable.GetFontFamilyCount(self);
    }

    pub inline fn GetFontFamily(self: *IDWriteFontCollection, index: UINT32, fontFamily: *?*IDWriteFontFamily) HRESULT {
        return self.vtable.GetFontFamily(self, index, fontFamily);
    }

    pub inline fn FindFamilyName(
        self: *IDWriteFontCollection,
        familyName: [*:0]const WCHAR,
        index: *UINT32,
        exists: *BOOL,
    ) HRESULT {
        return self.vtable.FindFamilyName(self, familyName, index, exists);
    }
};

// IDWriteFontCollection1 (extends IDWriteFontCollection)
// Adds slots 7-8 (all Reserved for our purposes)
pub const IDWriteFontCollection1 = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontCollection1) callconv(.winapi) u32,
        // IDWriteFontCollection (slots 3-6)
        GetFontFamilyCount: *const fn (*IDWriteFontCollection1) callconv(.winapi) UINT32,
        GetFontFamily: *const fn (
            *IDWriteFontCollection1,
            index: UINT32,
            fontFamily: *?*IDWriteFontFamily,
        ) callconv(.winapi) HRESULT,
        FindFamilyName: *const fn (
            *IDWriteFontCollection1,
            familyName: [*:0]const WCHAR,
            index: *UINT32,
            exists: *BOOL,
        ) callconv(.winapi) HRESULT,
        GetFontFromFontFace: Reserved,
        // IDWriteFontCollection1 (slots 7-8)
        _slot7: Reserved,
        _slot8: Reserved,
    };

    pub inline fn Release(self: *IDWriteFontCollection1) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFontFamilyCount(self: *IDWriteFontCollection1) UINT32 {
        return self.vtable.GetFontFamilyCount(self);
    }

    pub inline fn GetFontFamily(self: *IDWriteFontCollection1, index: UINT32, fontFamily: *?*IDWriteFontFamily) HRESULT {
        return self.vtable.GetFontFamily(self, index, fontFamily);
    }

    pub inline fn FindFamilyName(
        self: *IDWriteFontCollection1,
        familyName: [*:0]const WCHAR,
        index: *UINT32,
        exists: *BOOL,
    ) HRESULT {
        return self.vtable.FindFamilyName(self, familyName, index, exists);
    }
};

// IDWriteFactory3
// IUnknown slots 0-2
// IDWriteFactory slots 3-23: GetSystemFontCollection(3), ..., CreateNumberSubstitution(22), CreateGlyphRunAnalysis(23)
// IDWriteFactory1 slots 24-25: all Reserved
// IDWriteFactory2 slots 26-30: GetSystemFontFallback(26), [27-30 Reserved]
// IDWriteFactory3 slots 31-39: [31-37 Reserved], GetSystemFontCollection1(38), [39 Reserved]
pub const IDWriteFactory3 = extern struct {
    vtable: *const VTable,

    pub const IID = GUID{
        .data1 = 0x9A1B41C3,
        .data2 = 0xD3BB,
        .data3 = 0x466A,
        .data4 = .{ 0x87, 0xFC, 0xFE, 0x67, 0x55, 0x6A, 0x3B, 0x65 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFactory3) callconv(.winapi) u32,
        // IDWriteFactory (slots 3-23)
        GetSystemFontCollection: *const fn (
            *IDWriteFactory3,
            fontCollection: *?*IDWriteFontCollection,
            checkForUpdates: BOOL,
        ) callconv(.winapi) HRESULT,
        CreateCustomFontCollection: Reserved,
        RegisterFontCollectionLoader: Reserved,
        UnregisterFontCollectionLoader: Reserved,
        CreateFontFileReference: Reserved,
        CreateCustomFontFileReference: Reserved,
        CreateFontFace: Reserved,
        CreateRenderingParams: Reserved,
        CreateMonitorRenderingParams: Reserved,
        CreateCustomRenderingParams: Reserved,
        RegisterFontFileLoader: Reserved,
        UnregisterFontFileLoader: Reserved,
        CreateTextFormat: Reserved,
        CreateTypography: Reserved,
        GetGdiInterop: Reserved,
        CreateTextLayout: Reserved,
        CreateGdiCompatibleTextLayout: Reserved,
        CreateEllipsisTrimmingSign: Reserved,
        CreateTextAnalyzer: Reserved,
        CreateNumberSubstitution: *const fn (
            *IDWriteFactory3,
            method: u32,
            localeName: ?[*:0]const WCHAR,
            ignoreUserOverride: BOOL,
            numberSubstitution: *?*IDWriteNumberSubstitution,
        ) callconv(.winapi) HRESULT,
        CreateGlyphRunAnalysis: Reserved,
        // IDWriteFactory1 (slots 24-25)
        _slot24: Reserved,
        _slot25: Reserved,
        // IDWriteFactory2 (slots 26-30)
        GetSystemFontFallback: *const fn (
            *IDWriteFactory3,
            fontFallback: *?*IDWriteFontFallback,
        ) callconv(.winapi) HRESULT,
        _slot27: Reserved,
        _slot28: Reserved,
        _slot29: Reserved,
        _slot30: Reserved,
        // IDWriteFactory3 (slots 31-39)
        _slot31: Reserved,
        _slot32: Reserved,
        _slot33: Reserved,
        _slot34: Reserved,
        _slot35: Reserved,
        _slot36: Reserved,
        _slot37: Reserved,
        GetSystemFontCollection1: *const fn (
            *IDWriteFactory3,
            includeDownloadableFonts: BOOL,
            fontCollection: *?*IDWriteFontCollection1,
            checkForUpdates: BOOL,
        ) callconv(.winapi) HRESULT,
        _slot39: Reserved,
    };

    pub inline fn Release(self: *IDWriteFactory3) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetSystemFontCollection(
        self: *IDWriteFactory3,
        fontCollection: *?*IDWriteFontCollection,
        checkForUpdates: BOOL,
    ) HRESULT {
        return self.vtable.GetSystemFontCollection(self, fontCollection, checkForUpdates);
    }

    pub inline fn CreateNumberSubstitution(
        self: *IDWriteFactory3,
        method: u32,
        localeName: ?[*:0]const WCHAR,
        ignoreUserOverride: BOOL,
        numberSubstitution: *?*IDWriteNumberSubstitution,
    ) HRESULT {
        return self.vtable.CreateNumberSubstitution(self, method, localeName, ignoreUserOverride, numberSubstitution);
    }

    pub inline fn GetSystemFontFallback(
        self: *IDWriteFactory3,
        fontFallback: *?*IDWriteFontFallback,
    ) HRESULT {
        return self.vtable.GetSystemFontFallback(self, fontFallback);
    }

    pub inline fn GetSystemFontCollection1(
        self: *IDWriteFactory3,
        includeDownloadableFonts: BOOL,
        fontCollection: *?*IDWriteFontCollection1,
        checkForUpdates: BOOL,
    ) HRESULT {
        return self.vtable.GetSystemFontCollection1(self, includeDownloadableFonts, fontCollection, checkForUpdates);
    }
};

// --- Helper Functions ---

pub const DWriteCreateFactoryFn = *const fn (
    DWRITE_FACTORY_TYPE,
    *const GUID,
    *?*anyopaque,
) callconv(.winapi) HRESULT;

pub fn loadDWriteCreateFactory() !DWriteCreateFactoryFn {
    const dwrite_dll = std.os.windows.kernel32.LoadLibraryW(
        std.unicode.utf8ToUtf16LeStringLiteral("dwrite.dll"),
    ) orelse return error.DWriteNotAvailable;

    const proc = std.os.windows.kernel32.GetProcAddress(
        dwrite_dll,
        "DWriteCreateFactory",
    ) orelse return error.DWriteCreateFactoryNotFound;

    return @ptrCast(proc);
}

/// Read the string at index 0 from an IDWriteLocalizedStrings into a
/// UTF-8 slice backed by the provided buffer.
///
/// Covers all BMP characters, which includes every practical font name.
/// Surrogate pairs (non-BMP) are replaced with U+FFFD.
pub fn getLocalizedString(
    strings: *IDWriteLocalizedStrings,
    buf: []u8,
) ![]const u8 {
    // Get the length of the string at index 0 (in WCHARs, not including null).
    var wide_len: UINT32 = 0;
    const hr = strings.GetStringLength(0, &wide_len);
    if (FAILED(hr)) return error.GetStringLengthFailed;

    // Stack-allocate a wide buffer (512 WCHAR max).
    var wide_buf: [512]WCHAR = undefined;
    if (wide_len + 1 > wide_buf.len) return error.StringTooLong;

    const hr2 = strings.GetString(0, &wide_buf, wide_len + 1);
    if (FAILED(hr2)) return error.GetStringFailed;

    // Convert UTF-16LE to UTF-8 manually, handling BMP characters.
    var out_pos: usize = 0;
    var i: usize = 0;
    while (i < wide_len) : (i += 1) {
        const wc = wide_buf[i];
        const codepoint: u21 = if (wc >= 0xD800 and wc <= 0xDFFF)
            // Surrogate -- replace with U+FFFD (we only handle BMP).
            0xFFFD
        else
            wc;

        // Encode codepoint as UTF-8.
        if (codepoint < 0x80) {
            if (out_pos >= buf.len) return error.BufferTooSmall;
            buf[out_pos] = @intCast(codepoint);
            out_pos += 1;
        } else if (codepoint < 0x800) {
            if (out_pos + 1 >= buf.len) return error.BufferTooSmall;
            buf[out_pos] = @intCast(0xC0 | (codepoint >> 6));
            buf[out_pos + 1] = @intCast(0x80 | (codepoint & 0x3F));
            out_pos += 2;
        } else {
            if (out_pos + 2 >= buf.len) return error.BufferTooSmall;
            buf[out_pos] = @intCast(0xE0 | (codepoint >> 12));
            buf[out_pos + 1] = @intCast(0x80 | ((codepoint >> 6) & 0x3F));
            buf[out_pos + 2] = @intCast(0x80 | (codepoint & 0x3F));
            out_pos += 3;
        }
    }

    return buf[0..out_pos];
}

// --- Tests ---

test "vtable pointer sizes" {
    const ptr_size = @sizeOf(*anyopaque);
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFactory3));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontCollection));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontCollection1));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFamily));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFont));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFace));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFile));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFileLoader));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteLocalFontFileLoader));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFallback));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteLocalizedStrings));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteNumberSubstitution));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteTextAnalysisSource));
}

test "IID values" {
    // IDWriteFactory3: 9A1B41C3-D3BB-466A-87FC-FE67556A3B65
    try std.testing.expectEqual(IDWriteFactory3.IID.data1, 0x9A1B41C3);
    try std.testing.expectEqual(IDWriteFactory3.IID.data2, 0xD3BB);
    try std.testing.expectEqual(IDWriteFactory3.IID.data3, 0x466A);
    try std.testing.expectEqualSlices(u8, &IDWriteFactory3.IID.data4, &[8]u8{ 0x87, 0xFC, 0xFE, 0x67, 0x55, 0x6A, 0x3B, 0x65 });

    // IDWriteLocalFontFileLoader: b2d9f3ec-c9fe-4a11-a2ec-d86208f7c0a2
    try std.testing.expectEqual(IDWriteLocalFontFileLoader.IID.data1, 0xb2d9f3ec);
    try std.testing.expectEqual(IDWriteLocalFontFileLoader.IID.data2, 0xc9fe);
    try std.testing.expectEqual(IDWriteLocalFontFileLoader.IID.data3, 0x4a11);
    try std.testing.expectEqualSlices(u8, &IDWriteLocalFontFileLoader.IID.data4, &[8]u8{ 0xa2, 0xec, 0xd8, 0x62, 0x08, 0xf7, 0xc0, 0xa2 });
}

test "enum values" {
    try std.testing.expectEqual(@intFromEnum(DWRITE_FONT_WEIGHT.NORMAL), 400);
    try std.testing.expectEqual(@intFromEnum(DWRITE_FONT_WEIGHT.BOLD), 700);
    try std.testing.expectEqual(@intFromEnum(DWRITE_FONT_STYLE.NORMAL), 0);
    try std.testing.expectEqual(@intFromEnum(DWRITE_FONT_STYLE.ITALIC), 2);
}

test "DWRITE_UNICODE_RANGE size" {
    try std.testing.expectEqual(@sizeOf(DWRITE_UNICODE_RANGE), 8);
}
