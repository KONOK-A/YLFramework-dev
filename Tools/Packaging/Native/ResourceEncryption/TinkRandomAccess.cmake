# Only remove a redundant read-side ciphertext copy in the exact pinned implementation.
# The bridge still uses NewDecryptingRandomAccessStream, and segment parsing/authentication remain Tink's.
set(upstream "${tink_pin_SOURCE_DIR}/tink/subtle/decrypting_random_access_stream.cc")
file(SHA256 "${upstream}" upstream_sha)
if(NOT upstream_sha STREQUAL "d3c95ce423703685f1a60c2d9a179cc3604c43d3adefeda47a9c10f0c1447777")
  message(FATAL_ERROR "Tink random reader changed; review the bounded-capacity adaptation first")
endif()
file(READ "${upstream}" reader)

# Reuse one owning ciphertext vector as PRead's non-owning destination. The bridge always invokes
# the random-access stream within a single segment. Keep the upstream loop valid as well.
string(REPLACE [=[  auto ct_buffer_result = Buffer::New(ct_segment_size_);]=] [=[  std::vector<uint8_t> ciphertext(ct_segment_size_);
  auto ct_buffer_result = Buffer::NewNonOwning(reinterpret_cast<char*>(ciphertext.data()), ct_segment_size_);]=] reader "${reader}")
string(REPLACE [=[    auto dec_status = segment_decrypter_->DecryptSegment(
        std::vector<uint8_t>(ct_buffer->get_mem_block(),
                             ct_buffer->get_mem_block() + ct_buffer->size()),
        segment_nr, is_last_segment, pt_segment);]=] [=[    ciphertext->resize(ct_buffer->size());
    if (!pt_segment->empty()) OPENSSL_cleanse(pt_segment->data(), pt_segment->size());
    auto dec_status = segment_decrypter_->DecryptSegment(
        *ciphertext, segment_nr, is_last_segment, pt_segment);]=] reader "${reader}")
string(REPLACE "auto status =\n        ReadAndDecryptSegment(segment_nr, ct_buffer.get(), &pt_segment);"
  "ciphertext.resize(ct_segment_size_);\n    auto status = ReadAndDecryptSegment(segment_nr, ct_buffer.get(), &ciphertext, &pt_segment);"
  reader "${reader}")
string(REPLACE "int64_t segment_nr, Buffer* ct_buffer, std::vector<uint8_t>* pt_segment) {"
  "int64_t segment_nr, Buffer* ct_buffer, std::vector<uint8_t>* ciphertext, std::vector<uint8_t>* pt_segment) {"
  reader "${reader}")

# Scope the signature change to this generated TU and its private class declaration. Other users only
# consume the original public API; compile all inclusions against the same generated header.
set(upstream_header "${tink_pin_SOURCE_DIR}/tink/subtle/decrypting_random_access_stream.h")
file(READ "${upstream_header}" declaration)
string(REGEX REPLACE "(ReadAndDecryptSegment\\([^;]*Buffer\\* ct_buffer,)" "\\1 std::vector<uint8_t>* ciphertext,"
  declaration "${declaration}")

# Tink's temporary plaintext is now in a locally adapted function. Scrub it on scope exit, including
# failure, with the pinned BoringSSL primitive. The bridge still scrubs its own page and failed output.
string(REPLACE "#include <vector>" [=[#include <vector>

#include "openssl/mem.h"]=] reader "${reader}")
# Reserve capacity once so the plaintext vector neither doubles across segments nor frees an
# uncleared plaintext allocation while growing. Its size grows only to the current authenticated segment.
string(REPLACE "  std::vector<uint8_t> pt_segment;" [=[  std::vector<uint8_t> pt_segment;
  pt_segment.reserve(pt_segment_size_);
  struct ClearPlaintext {
    std::vector<uint8_t>& bytes;
    ~ClearPlaintext() { if (!bytes.empty()) OPENSSL_cleanse(bytes.data(), bytes.size()); }
  } clear_plaintext{pt_segment};]=] reader "${reader}")

if(YL_PAYLOAD_TRACE)
  string(REPLACE "#include <vector>" [=[#include <vector>

void YLObserveSegmentCapacity(std::size_t cipher, std::size_t plain) noexcept;]=] reader "${reader}")
  string(REPLACE "    if (status.ok() || status.code() == absl::StatusCode::kOutOfRange) {"
    "    YLObserveSegmentCapacity(ciphertext.capacity(), pt_segment.capacity());\n    if (status.ok() || status.code() == absl::StatusCode::kOutOfRange) {"
    reader "${reader}")
endif()

if(NOT declaration MATCHES "ct_buffer, std::vector<uint8_t>\\* ciphertext," OR
   NOT reader MATCHES "ReadAndDecryptSegment\\(segment_nr, ct_buffer.get\\(\\), &ciphertext, &pt_segment\\)" OR
   NOT reader MATCHES "DecryptSegment\\([ \r\n]+\\*ciphertext, segment_nr, is_last_segment, pt_segment\\)")
  message(FATAL_ERROR "Failed to apply the pinned Tink adaptation")
endif()

set(overlay "${CMAKE_CURRENT_BINARY_DIR}/yl-tink-overlay")
set(generated "${overlay}/tink/subtle/decrypting_random_access_stream.cc")
file(CONFIGURE OUTPUT "${generated}" CONTENT "${reader}" @ONLY NEWLINE_STYLE UNIX)
file(CONFIGURE OUTPUT "${overlay}/tink/subtle/decrypting_random_access_stream.h" CONTENT "${declaration}" @ONLY NEWLINE_STYLE UNIX)
get_target_property(random_reader tink::subtle::decrypting_random_access_stream ALIASED_TARGET)
get_target_property(streaming tink::subtle::nonce_based_streaming_aead ALIASED_TARGET)
set_property(TARGET "${random_reader}" PROPERTY SOURCES "${generated}")
target_include_directories("${random_reader}" BEFORE PRIVATE "${overlay}")
target_include_directories("${streaming}" BEFORE PRIVATE "${overlay}")
target_link_libraries("${random_reader}" PRIVATE crypto)
