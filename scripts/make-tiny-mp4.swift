// Makes tests/SkylightPhotoSync.Tests/Fixtures/Images/tiny.mp4: a 64x48, 2-frame H.264 MP4 under 1 KB.
// Run once on macOS: swiftc -O scripts/make-tiny-mp4.swift -o /tmp/mkmp4 && /tmp/mkmp4 tests/SkylightPhotoSync.Tests/Fixtures/Images/tiny.mp4
// The output is committed as a static fixture; scripts/make-fixtures.sh does not regenerate it.
import AVFoundation
import CoreVideo
let out = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.removeItem(at: out)
let w = 64, h = 48
let writer = try! AVAssetWriter(outputURL: out, fileType: .mp4)
let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
    AVVideoCodecKey: AVVideoCodecType.h264,
    AVVideoWidthKey: w, AVVideoHeightKey: h,
    AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: 20_000],
])
input.expectsMediaDataInRealTime = false
let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
    kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
    kCVPixelBufferWidthKey as String: w, kCVPixelBufferHeightKey as String: h])
writer.add(input)
writer.startWriting(); writer.startSession(atSourceTime: .zero)
var pb: CVPixelBuffer?
CVPixelBufferCreate(nil, w, h, kCVPixelFormatType_32BGRA, nil, &pb)
CVPixelBufferLockBaseAddress(pb!, [])
let base = CVPixelBufferGetBaseAddress(pb!)!.assumingMemoryBound(to: UInt8.self)
let stride = CVPixelBufferGetBytesPerRow(pb!)
for y in 0..<h { for x in 0..<w { let p = base + y*stride + x*4; p[0] = UInt8(x*4 % 256); p[1] = UInt8(y*5 % 256); p[2] = 128; p[3] = 255 } }
CVPixelBufferUnlockBaseAddress(pb!, [])
for i in 0..<2 {
    while !input.isReadyForMoreMediaData { Thread.sleep(forTimeInterval: 0.01) }
    adaptor.append(pb!, withPresentationTime: CMTime(value: CMTimeValue(i), timescale: 10))
}
input.markAsFinished()
let sem = DispatchSemaphore(value: 0)
writer.finishWriting { sem.signal() }
sem.wait()
print(writer.status.rawValue, writer.error ?? "ok")
