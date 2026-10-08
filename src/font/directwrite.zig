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
        // IUnknown
        QueryInterface: *const fn (*IDWriteNumberSubstitution, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteNumberSubstitution) callconv(.winapi) u32,
        Release: *const fn (*IDWriteNumberSubstitution) callconv(.winapi) u32,
    };

    pub inline fn Release(self: *IDWriteNumberSubstitution) u32 {
        return self.vtable.Release(self);
    }
};

// IDWriteLocalizedStrings
pub const IDWriteLocalizedStrings = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteLocalizedStrings) callconv(.winapi) u32,
        // IDWriteLocalizedStrings
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
pub const IDWriteFontFace = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFace) callconv(.winapi) u32,
        // IDWriteFontFace
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
        // IUnknown
        QueryInterface: *const fn (*IDWriteFontFileLoader, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteFontFileLoader) callconv(.winapi) u32,
        Release: *const fn (*IDWriteFontFileLoader) callconv(.winapi) u32,
        // IDWriteFontFileLoader
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
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteLocalFontFileLoader) callconv(.winapi) u32,
        // IDWriteFontFileLoader
        CreateStreamFromKey: Reserved,
        // IDWriteLocalFontFileLoader
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
pub const IDWriteFontFile = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFile) callconv(.winapi) u32,
        // IDWriteFontFile
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
        // IUnknown
        QueryInterface: *const fn (*IDWriteTextAnalysisSource, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDWriteTextAnalysisSource) callconv(.winapi) u32,
        Release: *const fn (*IDWriteTextAnalysisSource) callconv(.winapi) u32,
        // IDWriteTextAnalysisSource
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
pub const IDWriteFontFallback = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFallback) callconv(.winapi) u32,
        // IDWriteFontFallback
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
pub const IDWriteFont = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: *const fn (*IDWriteFont) callconv(.winapi) u32,
        Release: *const fn (*IDWriteFont) callconv(.winapi) u32,
        // IDWriteFont
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
        // IDWriteFont1 reserved padding before IDWriteFont2.IsColorFont
        _slot14: Reserved,
        _slot15: Reserved,
        _slot16: Reserved,
        _slot17: Reserved,
        // IDWriteFont2
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

// IDWriteFontList
// Slots: GetFontCollection(3), GetFontCount(4), GetFont(5)
// No IID: this list is only ever reached through the object that returns
// it, so it is never queried for.
pub const IDWriteFontList = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontList) callconv(.winapi) u32,
        // IDWriteFontList
        GetFontCollection: Reserved,
        GetFontCount: *const fn (*IDWriteFontList) callconv(.winapi) UINT32,
        GetFont: *const fn (*IDWriteFontList, index: UINT32, font: *?*IDWriteFont) callconv(.winapi) HRESULT,
    };

    pub inline fn Release(self: *IDWriteFontList) u32 {
        return self.vtable.Release(self);
    }

    pub inline fn GetFontCount(self: *IDWriteFontList) UINT32 {
        return self.vtable.GetFontCount(self);
    }

    pub inline fn GetFont(self: *IDWriteFontList, index: UINT32, font: *?*IDWriteFont) HRESULT {
        return self.vtable.GetFont(self, index, font);
    }
};

// IDWriteFontFamily (extends IDWriteFontList)
// Slots: GetFamilyNames(6), GetFirstMatchingFont(7), GetMatchingFonts(8)
pub const IDWriteFontFamily = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontFamily) callconv(.winapi) u32,
        // IDWriteFontList
        GetFontCollection: Reserved,
        GetFontCount: *const fn (*IDWriteFontFamily) callconv(.winapi) UINT32,
        GetFont: *const fn (*IDWriteFontFamily, index: UINT32, font: *?*IDWriteFont) callconv(.winapi) HRESULT,
        // IDWriteFontFamily
        GetFamilyNames: *const fn (*IDWriteFontFamily, names: *?*IDWriteLocalizedStrings) callconv(.winapi) HRESULT,
        GetFirstMatchingFont: Reserved,
        GetMatchingFonts: *const fn (
            *IDWriteFontFamily,
            weight: DWRITE_FONT_WEIGHT,
            stretch: DWRITE_FONT_STRETCH,
            style: DWRITE_FONT_STYLE,
            matchingFonts: *?*IDWriteFontList,
        ) callconv(.winapi) HRESULT,
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

    /// The family's fonts ranked best first for the requested weight,
    /// stretch and style. This is the same matcher Windows Terminal uses
    /// to pick a face, so we do not rank the family ourselves.
    pub inline fn GetMatchingFonts(
        self: *IDWriteFontFamily,
        weight: DWRITE_FONT_WEIGHT,
        stretch: DWRITE_FONT_STRETCH,
        style: DWRITE_FONT_STYLE,
        matchingFonts: *?*IDWriteFontList,
    ) HRESULT {
        return self.vtable.GetMatchingFonts(self, weight, stretch, style, matchingFonts);
    }
};

// IDWriteFontCollection
// Slots: GetFontFamilyCount(3), GetFontFamily(4), FindFamilyName(5), GetFontFromFontFace(6)
pub const IDWriteFontCollection = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontCollection) callconv(.winapi) u32,
        // IDWriteFontCollection
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
pub const IDWriteFontCollection1 = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFontCollection1) callconv(.winapi) u32,
        // IDWriteFontCollection
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
        // IDWriteFontCollection1
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
pub const IDWriteFactory3 = extern struct {
    vtable: *const VTable,

    pub const IID = GUID{
        .data1 = 0x9A1B41C3,
        .data2 = 0xD3BB,
        .data3 = 0x466A,
        .data4 = .{ 0x87, 0xFC, 0xFE, 0x67, 0x55, 0x6A, 0x3B, 0x65 },
    };

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*IDWriteFactory3) callconv(.winapi) u32,
        // IDWriteFactory
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
        // IDWriteFactory1
        _slot24: Reserved,
        _slot25: Reserved,
        // IDWriteFactory2
        GetSystemFontFallback: *const fn (
            *IDWriteFactory3,
            fontFallback: *?*IDWriteFontFallback,
        ) callconv(.winapi) HRESULT,
        _slot27: Reserved,
        _slot28: Reserved,
        _slot29: Reserved,
        _slot30: Reserved,
        // IDWriteFactory3
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

// Zig 0.16 dropped the dynamic-loading entry points from
// std.os.windows.kernel32, so declare them here: this is their only consumer.
extern "kernel32" fn LoadLibraryExW(
    lpLibFileName: [*:0]const u16,
    hFile: ?std.os.windows.HANDLE,
    dwFlags: std.os.windows.DWORD,
) callconv(.winapi) ?std.os.windows.HMODULE;

extern "kernel32" fn GetProcAddress(
    hModule: std.os.windows.HMODULE,
    lpProcName: [*:0]const u8,
) callconv(.winapi) ?std.os.windows.FARPROC;

extern "kernel32" fn GetModuleFileNameW(
    hModule: ?std.os.windows.HMODULE,
    lpFilename: [*]u16,
    nSize: std.os.windows.DWORD,
) callconv(.winapi) std.os.windows.DWORD;

extern "kernel32" fn GetEnvironmentVariableW(
    lpName: [*:0]const u16,
    lpBuffer: [*]u16,
    nSize: std.os.windows.DWORD,
) callconv(.winapi) std.os.windows.DWORD;

/// Resolve system libraries from System32 only. A bare-name load
/// consults the loader's search order, whose first entry is the
/// directory of the executable, so a dwrite.dll dropped beside Wintty
/// (or beside the test binary, or in the working directory) would be
/// mapped and its DWriteCreateFactory called with the process's
/// privileges. dwrite.dll is an in-box DLL that no tier ships, so
/// there is nothing to gain from letting the search order pick it.
const LOAD_LIBRARY_SEARCH_SYSTEM32: std.os.windows.DWORD = 0x0000_0800;

/// Load dwrite.dll from System32, or null when it is not there. Split
/// out so a test can name the module that was actually mapped.
pub fn loadDWrite() ?std.os.windows.HMODULE {
    return LoadLibraryExW(
        std.unicode.utf8ToUtf16LeStringLiteral("dwrite.dll"),
        null,
        LOAD_LIBRARY_SEARCH_SYSTEM32,
    );
}

pub fn loadDWriteCreateFactory() !DWriteCreateFactoryFn {
    // The module is intentionally never freed: the returned pointer
    // outlives this call, and unloading dwrite.dll under it would make
    // it dangle. One module for the life of the process is the intent.
    const dwrite_dll = loadDWrite() orelse return error.DWriteNotAvailable;

    const proc = GetProcAddress(
        dwrite_dll,
        "DWriteCreateFactory",
    ) orelse return error.DWriteCreateFactoryNotFound;

    // FARPROC's pointee is align-1; a fn pointer is align-4 on aarch64,
    // so @alignCast asserts what the loader already guarantees.
    return @ptrCast(@alignCast(proc));
}

/// Read the string at index 0 from an IDWriteLocalizedStrings into a
/// UTF-8 slice backed by the provided buffer.
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

    const out_len = std.unicode.utf16LeToUtf8(buf, wide_buf[0..wide_len]) catch
        return error.BufferTooSmall;

    return buf[0..out_len];
}

// --- Tests ---

test "vtable pointer sizes" {
    const ptr_size = @sizeOf(*anyopaque);
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFactory3));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontCollection));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontCollection1));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontFamily));
    try std.testing.expectEqual(ptr_size, @sizeOf(IDWriteFontList));
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

test "dwrite.dll is resolved from System32, not from the search order" {
    const builtin = @import("builtin");
    if (builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    const alloc = testing.allocator;

    // A same-named DLL in the working directory, loadable but without
    // DWriteCreateFactory: the shape of the file a search-order load
    // would pick up instead of the real one.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();

    var root_w: [std.fs.max_path_bytes]u16 = undefined;
    const root_len = GetEnvironmentVariableW(
        std.unicode.utf8ToUtf16LeStringLiteral("SystemRoot"),
        &root_w,
        root_w.len,
    );
    if (root_len == 0 or root_len >= root_w.len) return error.SkipZigTest;

    var root_buf: [std.fs.max_path_bytes]u8 = undefined;
    const root_len8 = std.unicode.utf16LeToUtf8(&root_buf, root_w[0..root_len]) catch
        return error.SkipZigTest;
    const source = try std.fs.path.join(alloc, &.{ root_buf[0..root_len8], "System32", "version.dll" });
    defer alloc.free(source);

    const work_dir = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);
    defer alloc.free(work_dir);
    const plant = try std.fs.path.join(alloc, &.{ work_dir, "dwrite.dll" });
    defer alloc.free(plant);
    std.Io.Dir.copyFileAbsolute(source, plant, testing.io, .{}) catch
        return error.SkipZigTest;

    // The same plant BESIDE THE TEST BINARY: the application directory is
    // FIRST in the old bare-name order, so only a plant there can turn the
    // old loader red - a working-directory plant sits behind System32 and
    // guards nothing. This is the location a hijack actually uses.
    var exe_w: [std.fs.max_path_bytes]u16 = undefined;
    const exe_len = GetModuleFileNameW(null, &exe_w, exe_w.len);
    if (exe_len == 0 or exe_len >= exe_w.len) return error.SkipZigTest;
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const exe_len8 = try std.unicode.utf16LeToUtf8(&exe_buf, exe_w[0..exe_len]);
    const exe_dir = std.fs.path.dirname(exe_buf[0..exe_len8]) orelse
        return error.SkipZigTest;
    const plant_exe = try std.fs.path.join(alloc, &.{ exe_dir, "dwrite.dll" });
    defer alloc.free(plant_exe);
    std.Io.Dir.copyFileAbsolute(source, plant_exe, testing.io, .{}) catch
        return error.SkipZigTest;
    defer std.Io.Dir.deleteFileAbsolute(testing.io, plant_exe) catch {};

    var saved: [std.fs.max_path_bytes]u8 = undefined;
    const saved_len = try std.process.currentPath(testing.io, &saved);
    defer std.process.setCurrentPath(testing.io, saved[0..saved_len]) catch {};
    try std.process.setCurrentPath(testing.io, work_dir);

    // Resolves, and the module behind it is System32's, not the plant.
    const module = loadDWrite() orelse return error.TestExpectedEqual;
    var wide: [std.fs.max_path_bytes]u16 = undefined;
    const len = GetModuleFileNameW(module, &wide, wide.len);
    if (len == 0 or len >= wide.len) return error.TestExpectedEqual;
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const path_len = try std.unicode.utf16LeToUtf8(&path_buf, wide[0..len]);
    const path = path_buf[0..path_len];
    try testing.expect(std.ascii.indexOfIgnoreCase(path, "\\System32\\") != null);
    try testing.expect(std.ascii.endsWithIgnoreCase(path, "\\dwrite.dll"));

    // And the factory is reachable through the same module, which a
    // plant without the export could not answer.
    const factory = try loadDWriteCreateFactory();
    try testing.expect(@intFromPtr(factory) != 0);
}
