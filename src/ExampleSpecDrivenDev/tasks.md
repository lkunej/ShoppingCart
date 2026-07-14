# Implementation Plan: Guest Cart Persistence

## Overview

This plan implements anonymous guest cart functionality for the CartService, extending the existing Cart entity with a nullable `GuestSessionToken`, adding a new `GuestCartController`, merge endpoint, rate limiting via Redis sorted sets, and a background cleanup service. The AuthService login endpoint is modified to pass through the guest session token. Property-based tests use FsCheck with xUnit.

## Tasks

- [ ] 1. Database migration and Cart entity extension
  - [ ] 1.1 Extend Cart entity with GuestSessionToken property
    - Add `public Guid? GuestSessionToken { get; set; }` to `Cart.cs`
    - Update `CartConfiguration` (or create if not present) to configure the filtered unique index on `GuestSessionToken` and filtered index on `UpdatedAt` for non-null `GuestSessionToken`
    - _Requirements: 1.1, 9.1_

  - [ ] 1.2 Create EF Core migration for GuestSessionToken column
    - Run `dotnet ef migrations add AddGuestSessionToken` in CartService project
    - Verify migration adds: nullable `GuestSessionToken` UUID column, unique filtered index `IX_Carts_GuestSessionToken`, filtered index `IX_Carts_GuestSessionToken_UpdatedAt` on `UpdatedAt` where `GuestSessionToken IS NOT NULL`
    - _Requirements: 1.1, 9.1_

- [ ] 2. DTOs and shared models for guest cart
  - [ ] 2.1 Create GuestCartResponse and MergeResponse DTOs
    - Create `GuestCartResponse` record with `GuestSessionToken`, `CartId`, `Items`, `TotalPrice`, `CreatedAt`, `UpdatedAt`
    - Create `MergeResponse` record with `CartId`, `UserId`, `Items`, `TotalPrice`, `Adjustments`, `StockValidationSkipped`, `CartItemLimitReached`
    - Create `MergeAdjustment` record with `ProductId`, `ProductName`, `OriginalGuestQuantity`, `OriginalAuthQuantity`, `MergedQuantity`, `Reason`
    - Place DTOs in `CartService/Models/DTOs/` or existing shared DTO location
    - _Requirements: 3.1, 6.1, 6.2, 6.3, 6.4_

- [ ] 3. Guest rate limiter service (Redis sorted sets)
  - [ ] 3.1 Create IGuestRateLimiterService interface and implementation
    - Define `IGuestRateLimiterService` with `CheckSessionCreationLimit(string ipAddress)` returning `GuestRateLimitResult`
    - Implement using Redis sorted sets: key `guest_rate:{ip}`, ZREMRANGEBYSCORE to prune expired, ZCARD to count, ZADD + EXPIRE on allowed
    - Max 10 sessions per IP per 60-minute sliding window
    - Calculate `RetryAfterSeconds` from oldest entry in sorted set when limit exceeded
    - Use Polly circuit breaker (existing resilience pipeline pattern)
    - _Requirements: 9.2, 9.3_

  - [ ]* 3.2 Write property test for guest rate limiting (Property 12)
    - **Property 12: Guest session creation rate limiting**
    - After 10 session creations within 60 minutes, subsequent attempts from same IP are rejected with 429
    - **Validates: Requirements 9.2, 9.3**

- [ ] 4. Implement IGuestCartService
  - [ ] 4.1 Create IGuestCartService interface
    - Define `CreateSession()`, `GetCart(Guid guestSessionToken)`, `AddItem(Guid guestSessionToken, Guid productId, int quantity)`, `UpdateItem(Guid guestSessionToken, Guid itemId, int quantity)`, `RemoveItem(Guid guestSessionToken, Guid itemId)`, `GetCartEntity(Guid guestSessionToken)`, `DeleteGuestCart(Guid guestSessionToken)`
    - Place in `CartService/Services/` alongside existing ICartService
    - _Requirements: 1.1, 2.1, 2.2, 2.3, 2.4_

  - [ ] 4.2 Implement GuestCartService
    - Implement `CreateSession`: generate UUID v4, create Cart with `UserId = Guid.Empty` and `GuestSessionToken = newToken`, persist to DB, cache in Redis with key `guest_cart:{token}` and 10-day TTL
    - Implement `GetCart`: check Redis cache first (`guest_cart:{token}`), fallback to PostgreSQL query by `GuestSessionToken`, repopulate cache on miss
    - Implement `AddItem`: validate 50-item limit, call `IInventoryClient.CheckAvailabilityWithProductInfo`, persist item, update Redis cache, reset 10-day TTL on writes
    - Implement `UpdateItem`: find item by ID in guest cart, verify stock, update quantity (1-999 range), update cache
    - Implement `RemoveItem`: find item by ID, remove from DB and cache
    - Implement `DeleteGuestCart`: remove cart + items from DB, invalidate Redis cache entry
    - Use same cache-aside pattern as existing CartService
    - _Requirements: 1.1, 1.2, 1.5, 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 3.3, 3.4, 3.5, 3.6, 9.4, 9.5_

  - [ ]* 4.3 Write property test for add/retrieve round-trip (Property 1)
    - **Property 1: Guest cart add/retrieve round-trip**
    - For any valid product and quantity within stock limits, adding then retrieving returns same item data
    - **Validates: Requirements 1.3, 2.1, 3.3**

  - [ ]* 4.4 Write property test for update quantity correctness (Property 4)
    - **Property 4: Update quantity correctness**
    - For any valid quantity (1–999), updating then retrieving shows the new quantity
    - **Validates: Requirements 2.2**

  - [ ]* 4.5 Write property test for remove item correctness (Property 5)
    - **Property 5: Remove item correctness**
    - Removing one item from N items results in N-1 items and item is gone
    - **Validates: Requirements 2.3**

  - [ ]* 4.6 Write property test for quantity constraint enforcement (Property 6)
    - **Property 6: Quantity constraint enforcement**
    - Invalid quantities (< 1 or > 9999 for Add, < 1 or > 999 for Update) are rejected with 400 and cart unchanged
    - **Validates: Requirements 2.5**

  - [ ]* 4.7 Write property test for stock validation enforcement (Property 7)
    - **Property 7: Stock validation enforcement**
    - Requesting quantity exceeding stock is rejected with 409 and cart unchanged
    - **Validates: Requirements 2.6**

  - [ ]* 4.8 Write property test for 50-item limit (Property 13)
    - **Property 13: Guest cart 50-item limit**
    - A cart at 50 items rejects new distinct items with 400
    - **Validates: Requirements 9.4**

- [ ] 5. Implement GuestCartController
  - [ ] 5.1 Create GuestCartController with all endpoints
    - `[ApiController]`, `[Route("api/guest-cart")]`, `[AllowAnonymous]`
    - `POST /api/guest-cart/items` — Add item (creates session if no valid X-Guest-Session, checks rate limit for new sessions)
    - `GET /api/guest-cart` — Get cart contents
    - `PUT /api/guest-cart/items/{itemId}` — Update item quantity
    - `DELETE /api/guest-cart/items/{itemId}` — Remove item
    - Validate X-Guest-Session header: reject non-UUID with 400, treat unknown UUID as new session (201)
    - Ignore any JWT Authorization header on these endpoints
    - Return `GuestCartResponse` with `guestSessionToken` in all responses
    - _Requirements: 1.1, 1.3, 1.4, 1.5, 7.1, 7.2, 7.3, 7.4, 7.6_

  - [ ]* 5.2 Write property test for token format and presence (Property 2)
    - **Property 2: Guest session token format and presence**
    - All guest cart operation responses include a valid UUID v4 `guestSessionToken`
    - **Validates: Requirements 1.2, 3.1**

  - [ ]* 5.3 Write property test for invalid token rejection (Property 3)
    - **Property 3: Invalid token rejection**
    - Any non-UUID string as X-Guest-Session results in HTTP 400
    - **Validates: Requirements 1.4, 7.3, 8.4**

- [ ] 6. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 7. Implement ICartMergeService
  - [ ] 7.1 Create ICartMergeService interface and CartMergeService implementation
    - Define `MergeGuestCart(Guid userId, Guid guestSessionToken)` returning `MergeResult`
    - Implement merge algorithm: load guest cart, load or create auth cart, merge items using max-quantity conflict resolution (cap at 9999), respect 50-item limit, run stock validation pass
    - On inventory service failure: complete merge without stock validation, set `StockValidationSkipped = true`
    - After successful merge: delete guest cart and invalidate session token
    - Track adjustments for conflict resolution and stock limitations
    - Return `MergeResult` with merged cart data and adjustment summary
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8, 6.1, 6.2, 6.3, 6.4_

  - [ ]* 7.2 Write property test for merge correctness (Property 8)
    - **Property 8: Merge correctness — union with max-quantity conflict resolution**
    - Products only in guest appear with guest quantity; only in auth appear unchanged; in both appear with max capped at 9999
    - **Validates: Requirements 5.1, 5.2, 5.3, 5.4**

  - [ ]* 7.3 Write property test for guest cart deletion after merge (Property 9)
    - **Property 9: Guest cart deletion after successful merge**
    - After merge, the guest cart token no longer resolves to an existing cart
    - **Validates: Requirements 5.5**

  - [ ]* 7.4 Write property test for merge stock and item-limit constraints (Property 10)
    - **Property 10: Merge respects stock and item-limit constraints**
    - Merged cart has ≤50 items, each quantity ≤ available stock, items with 0 stock removed
    - **Validates: Requirements 5.6, 5.8**

  - [ ]* 7.5 Write property test for merge adjustment reporting (Property 11)
    - **Property 11: Merge adjustment reporting correctness**
    - Adjustments are present when final quantity differs from both originals; absent when no such difference exists
    - **Validates: Requirements 6.1, 6.2**

- [ ] 8. Add merge endpoint to CartController
  - [ ] 8.1 Add POST /api/cart/merge endpoint
    - Add `[HttpPost("merge")]` action on existing `CartController`
    - Requires JWT authentication (existing `[Authorize]`)
    - Read `X-Guest-Session` header, validate UUID format (400 if invalid/missing)
    - Call `ICartMergeService.MergeGuestCart(userId, guestSessionToken)`
    - If guest cart expired/not found or empty: return 200 with auth cart unchanged
    - Return `MergeResponse` with items, total, adjustments, and flags
    - _Requirements: 8.3, 8.4, 8.5, 8.6, 8.7_

- [ ] 9. Modify AuthService login to pass through guest session token
  - [ ] 9.1 Update AuthController login and LoginResponse
    - Read `X-Guest-Session` header in `POST /auth/login`
    - If header present and value is valid UUID: include `GuestSessionToken` field in `LoginResponse`
    - If header absent or invalid: omit field (null) in response
    - Update `LoginResponse` record to include `string? GuestSessionToken = null`
    - _Requirements: 8.1_

  - [ ]* 9.2 Write property test for auth token pass-through (Property 14)
    - **Property 14: Auth login passes through guest session token**
    - Valid UUID in X-Guest-Session header appears in login response; invalid/absent means field is null
    - **Validates: Requirements 8.1**

- [ ] 10. Implement GuestCartCleanupService (BackgroundService)
  - [ ] 10.1 Create GuestCartCleanupService as BackgroundService
    - Inherit from `BackgroundService`, run timer every 60 minutes
    - Query for guest carts where `GuestSessionToken IS NOT NULL` and `UpdatedAt < UtcNow - 10 days`
    - Delete in batches of 100 per cycle
    - On individual cart delete failure: log error, skip cart, continue with remaining batch
    - On Redis invalidation failure after DB delete: log warning, continue (Redis TTL handles eviction)
    - Use `IServiceScopeFactory` to create scoped `CartDbContext` instances
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5_

  - [ ]* 10.2 Write unit tests for GuestCartCleanupService
    - Test batch processing deletes up to 100 expired carts
    - Test individual failure does not stop batch
    - Test Redis failure after DB delete logs warning and continues
    - Test non-expired carts are not deleted
    - _Requirements: 4.1, 4.2, 4.4, 4.5_

- [ ] 11. DI registration and wiring
  - [ ] 11.1 Register all new services in CartService Program.cs
    - Register `IGuestCartService` / `GuestCartService` as Scoped
    - Register `ICartMergeService` / `CartMergeService` as Scoped
    - Register `IGuestRateLimiterService` / `GuestRateLimiterService` as Singleton
    - Register `GuestCartCleanupService` as HostedService
    - Ensure `[AllowAnonymous]` on `GuestCartController` works with existing auth middleware (no additional policy needed)
    - _Requirements: 7.1, 7.5_

- [ ] 12. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties using FsCheck with xUnit (`FsCheck.Xunit`)
- Unit tests validate specific examples and edge cases
- The design uses C# (.NET 8) throughout — all implementations follow existing project patterns (EF Core, Polly v8, StackExchange.Redis)
- Guest carts use `UserId = Guid.Empty` and non-null `GuestSessionToken` to distinguish from authenticated carts
- The merge endpoint lives on the existing `CartController` since it requires JWT authentication

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "2.1"] },
    { "id": 1, "tasks": ["1.2", "3.1"] },
    { "id": 2, "tasks": ["3.2", "4.1"] },
    { "id": 3, "tasks": ["4.2"] },
    { "id": 4, "tasks": ["4.3", "4.4", "4.5", "4.6", "4.7", "4.8", "5.1"] },
    { "id": 5, "tasks": ["5.2", "5.3", "7.1"] },
    { "id": 6, "tasks": ["7.2", "7.3", "7.4", "7.5", "8.1"] },
    { "id": 7, "tasks": ["9.1", "10.1"] },
    { "id": 8, "tasks": ["9.2", "10.2", "11.1"] }
  ]
}
```
