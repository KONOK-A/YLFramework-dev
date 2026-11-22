#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <array>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>

#include "tink/subtle/aes_gcm_hkdf_streaming.h"

namespace {
using crypto::tink::OutputStream;
using crypto::tink::RandomAccessStream;
using crypto::tink::StreamingAead;
using crypto::tink::subtle::AesGcmHkdfStreaming;
using crypto::tink::subtle::HashType;
using crypto::tink::util::Buffer;
constexpr int kSegment = 1024 * 1024;
constexpr int kPlainSegment = kSegment - 16;
constexpr int kFirstSegment = kPlainSegment - 40;
constexpr int kWindow = 512 * 1024;
constexpr int kReadLimit = 128 * 1024;

auto Primitive(const char* key) {
  return AesGcmHkdfStreaming::New({crypto::tink::util::SecretDataFromStringView({key, 32}),
                                   HashType::SHA256, 32, kSegment, 0});
}

class CipherSource final : public RandomAccessStream {
 public:
  explicit CipherSource(HANDLE input) {
    const auto self = GetCurrentProcess();
    if (!DuplicateHandle(self, input, self, &handle_, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
      handle_ = nullptr;
    }
  }
  ~CipherSource() override { if (handle_) CloseHandle(handle_); }
  bool valid() const { return handle_ != nullptr; }
  int64_t bytes() const { return bytes_; }

  absl::StatusOr<int64_t> size() override {
    LARGE_INTEGER result;
    if (!GetFileSizeEx(handle_, &result)) return absl::DataLossError("cipher length");
    return result.QuadPart;
  }

  absl::Status PRead(int64_t position, int count, Buffer* buffer) override {
    if (!buffer || position < 0 || count <= 0 || count > buffer->allocated_size()) {
      return absl::InvalidArgumentError("cipher range");
    }
    // The managed reader serializes calls, and exclusively borrows the file position after SHA-256 validation.
    LARGE_INTEGER offset;
    offset.QuadPart = position;
    if (!SetFilePointerEx(handle_, offset, nullptr, FILE_BEGIN)) return absl::DataLossError("cipher seek");
    DWORD bytes = 0;
    if (!ReadFile(handle_, buffer->get_mem_block(), count, &bytes, nullptr)) return absl::DataLossError("cipher read");
    bytes_ += bytes;
    auto status = buffer->set_size(static_cast<int>(bytes));
    if (!status.ok()) return status;
    return bytes == static_cast<DWORD>(count) ? absl::OkStatus() : absl::OutOfRangeError("cipher EOF");
  }

 private:
  HANDLE handle_ = nullptr;
  int64_t bytes_ = 0;
};

class CipherDestination final : public OutputStream {
 public:
  explicit CipherDestination(HANDLE output) : handle_(output) {}

  absl::StatusOr<int> Next(void** data) override {
    auto status = Flush();
    if (!status.ok()) return status;
    *data = buffer_.data();
    used_ = static_cast<int>(buffer_.size());
    return used_;
  }

  void BackUp(int count) override { if (count >= 0 && count <= used_) used_ -= count; }
  int64_t Position() const override { return flushed_ + used_; }

  absl::Status Close() override {
    auto status = Flush();
    if (!status.ok()) return status;
    return FlushFileBuffers(handle_) ? absl::OkStatus() : absl::DataLossError("cipher flush");
  }

 private:
  absl::Status Flush() {
    int offset = 0;
    while (offset < used_) {
      DWORD written = 0;
      if (!WriteFile(handle_, buffer_.data() + offset, used_ - offset, &written, nullptr) || written == 0) {
        return absl::DataLossError("cipher write");
      }
      offset += static_cast<int>(written);
    }
    flushed_ += used_;
    used_ = 0;
    return absl::OkStatus();
  }

  HANDLE handle_;
  std::array<uint8_t, 64 * 1024> buffer_{};
  int64_t flushed_ = 0;
  int used_ = 0;
};

struct Reader {
  std::unique_ptr<StreamingAead> primitive;
  std::unique_ptr<RandomAccessStream> stream;
  const CipherSource* source;
  int64_t length;
  int64_t served = 0;
  int64_t pages = 0;
  int64_t page_start = -1;
  int page_count = 0;
  std::array<char, kWindow> page;

  ~Reader() { SecureZeroMemory(page.data(), page.size()); }

  bool ReadPage(int64_t position) {
    const auto segment = position < kFirstSegment ? 0 :
        kFirstSegment + (position - kFirstSegment) / kPlainSegment * kPlainSegment;
    const auto start = segment + (position - segment) / kWindow * kWindow;
    if (start == page_start) return true;
    page_start = -1;
    page_count = static_cast<int>(std::min({length - start,
        segment + (segment == 0 ? kFirstSegment : kPlainSegment) - start, static_cast<int64_t>(kWindow)}));
    auto buffer = Buffer::NewNonOwning(page.data(), page_count);
    if (!buffer.ok()) return false;
    auto status = stream->PRead(start, page_count, buffer->get());
    if ((!status.ok() && !absl::IsOutOfRange(status)) || (*buffer)->size() != page_count) return false;
    page_start = start;
    pages++;
    return true;
  }
};
}  // namespace

extern "C" __declspec(dllexport) int yl_seal(HANDLE plain, HANDLE cipher, const char* key,
                                            const char* aad, int aad_size) noexcept {
  try {
    if (!plain || !cipher || !key || !aad || aad_size <= 0) return -1;
    auto primitive = Primitive(key);
    if (!primitive.ok()) return -2;
    if (!SetFilePointerEx(plain, LARGE_INTEGER{}, nullptr, FILE_BEGIN)) return -3;
    auto stream = (*primitive)->NewEncryptingStream(
        std::make_unique<CipherDestination>(cipher), {aad, static_cast<size_t>(aad_size)});
    if (!stream.ok()) return -4;
    while (true) {
      void* buffer = nullptr;
      auto capacity = (*stream)->Next(&buffer);
      if (!capacity.ok() || *capacity <= 0) return -5;
      DWORD read = 0;
      while (read < static_cast<DWORD>(*capacity)) {
        DWORD next = 0;
        if (!ReadFile(plain, static_cast<uint8_t*>(buffer) + read, *capacity - read, &next, nullptr)) return -6;
        if (next == 0) {
          (*stream)->BackUp(*capacity - static_cast<int>(read));
          return (*stream)->Close().ok() ? 0 : -7;
        }
        read += next;
      }
    }
  } catch (...) { return -8; }
}

extern "C" __declspec(dllexport) void* yl_open(HANDLE cipher, const char* key, const char* aad,
                                              int aad_size, int64_t expected_length) noexcept {
  try {
    if (!cipher || !key || !aad || aad_size <= 0 || expected_length <= 0) return nullptr;
    auto file = std::make_unique<CipherSource>(cipher);
    const auto* source = file.get();
    auto primitive = Primitive(key);
    if (!file->valid() || !primitive.ok()) return nullptr;
    auto stream = (*primitive)->NewDecryptingRandomAccessStream(std::move(file), {aad, static_cast<size_t>(aad_size)});
    if (!stream.ok()) return nullptr;
    auto size = (*stream)->size();
    if (!size.ok() || *size != expected_length) return nullptr;
    return new Reader{std::move(*primitive), std::move(*stream), source, *size, 0, 0, -1, 0, {}};
  } catch (...) { return nullptr; }
}

extern "C" __declspec(dllexport) int yl_read(void* instance, int64_t position, char* output, int count) noexcept {
  if (!instance || !output || position < 0 || count <= 0 || count > kReadLimit) return -1;
  auto* reader = static_cast<Reader*>(instance);
  if (position > reader->length || count > reader->length - position) return -1;
  try {
    int copied = 0;
    while (copied < count && reader->ReadPage(position + copied)) {
      const int offset = static_cast<int>(position + copied - reader->page_start);
      const int portion = std::min(count - copied, reader->page_count - offset);
      std::memcpy(output + copied, reader->page.data() + offset, portion);
      copied += portion;
    }
    if (copied == count) {
      reader->served += count;
      return count;
    }
  } catch (...) {}
  reader->page_start = -1;
  SecureZeroMemory(reader->page.data(), reader->page.size());
  SecureZeroMemory(output, count);
  return -3;
}

extern "C" __declspec(dllexport) void yl_read_statistics(void* instance, int64_t* served, int64_t* source,
                                                        int64_t* pages, int64_t* native_payload_bound) noexcept {
  const auto* reader = static_cast<const Reader*>(instance);
  *served = reader->served;
  *source = reader->source->bytes();
  *pages = reader->pages;
  // Tink 6e968285 allocates the input Buffer, a copy for DecryptSegment, and its output vector.
  *native_payload_bound = 3LL * kSegment + kWindow;
}

extern "C" __declspec(dllexport) void yl_close(void* instance) noexcept {
  delete static_cast<Reader*>(instance);
}
