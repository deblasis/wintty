//! DX12 texture sampler stub.
//!
//! Will be replaced with a real implementation using a sampler descriptor
//! in a GPU-visible descriptor heap.

pub const Options = struct {};

pub const Error = error{
    SamplerCreateFailed,
};

pub fn init(opts: Options) Error!@This() {
    _ = opts;
    return .{};
}

pub fn deinit(self: @This()) void {
    _ = self;
}
