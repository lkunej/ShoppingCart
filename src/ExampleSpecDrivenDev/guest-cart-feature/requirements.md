# Requirements Document

## Introduction

This feature enables anonymous (guest) shoppers to add items to a cart without authenticating first. The guest cart is persisted in the browser via a session token, so refreshing the page or returning later from the same browser retains the cart. When the guest subsequently logs in, the system merges the guest cart into the authenticated user's existing cart, handling item conflicts gracefully.

## Glossary

- **Cart_Service**: The backend microservice responsible for cart operations (CRUD, merge, checkout). Currently requires authentication for all endpoints.
- **Guest_Session_Token**: A unique, opaque identifier (UUID) generated server-side and returned to the client. It identifies a guest cart without requiring authentication.
- **Guest_Cart**: A cart associated with a Guest_Session_Token rather than a registered user. Stored in the same persistence layer (PostgreSQL + Redis) as authenticated carts.
- **Authenticated_Cart**: A cart associated with a registered user's UserId, requiring a valid JWT for access.
- **Cart_Merge**: The process of combining items from a Guest_Cart into an Authenticated_Cart when a guest logs in.
- **Conflict_Resolution**: The strategy used when the same product exists in both the Guest_Cart and the Authenticated_Cart during Cart_Merge.
- **API_Gateway**: The gateway layer that validates JWT tokens and forwards X-User-Id headers to downstream services.
- **Auth_Service**: The authentication microservice handling login, token issuance, and token refresh.

## Requirements

### Requirement 1: Guest Session Creation

**User Story:** As a shopper, I want to receive a guest session when I visit the store without logging in, so that I can start adding items to a cart immediately.

#### Acceptance Criteria

1. WHEN a client sends a request to any guest cart endpoint without an X-Guest-Session header, THE Cart_Service SHALL generate a new Guest_Session_Token, create an empty Guest_Cart associated with that token, and return the Guest_Session_Token in the response body with HTTP 201.
2. THE Cart_Service SHALL generate Guest_Session_Tokens as UUID v4 values to ensure uniqueness.
3. WHEN a client sends a request with a valid existing Guest_Session_Token via the X-Guest-Session header, THE Cart_Service SHALL associate the request with the corresponding Guest_Cart and process the operation against that cart.
4. IF a client sends a request with a Guest_Session_Token that is not a valid UUID v4 format, THEN THE Cart_Service SHALL reject the request with HTTP 400 and an error message indicating a malformed session token.
5. IF a client sends a request with a Guest_Session_Token that is a valid UUID v4 but does not match any existing non-expired Guest_Cart, THEN THE Cart_Service SHALL treat the request as a new guest session by generating a new Guest_Session_Token and empty Guest_Cart, returning the new token in the response body with HTTP 201.

### Requirement 2: Guest Cart Operations

**User Story:** As a guest shopper, I want to add, update, and remove items from my cart, so that I can build my order before deciding to log in.

#### Acceptance Criteria

1. WHEN a guest adds an item to a Guest_Cart, THE Cart_Service SHALL verify stock availability via IInventoryClient.CheckAvailabilityWithProductInfo(productId, quantity), persist the item to PostgreSQL, and update the Redis cache, using the same persistence logic as Authenticated_Cart operations.
2. WHEN a guest updates the quantity of an existing item in a Guest_Cart, THE Cart_Service SHALL set the quantity to the requested value and verify stock availability via IInventoryClient.CheckAvailabilityWithProductInfo(productId, quantity).
3. WHEN a guest removes an item from a Guest_Cart, THE Cart_Service SHALL delete the item from the Guest_Cart and update the cache.
4. WHEN a guest retrieves a Guest_Cart that contains items, THE Cart_Service SHALL return the full cart representation including items, quantities, product names, unit prices, and total price.
5. THE Cart_Service SHALL enforce the same quantity constraints on Guest_Cart items as on Authenticated_Cart items (minimum 1, maximum 9999 for AddItem; minimum 1, maximum 999 for UpdateItem).
6. IF a guest adds or updates an item and the requested quantity exceeds available stock, THEN THE Cart_Service SHALL reject the request with HTTP 409 and an error response indicating insufficient stock and the available quantity.
7. IF a guest adds an item with a productId that does not exist in inventory, THEN THE Cart_Service SHALL reject the request with HTTP 404 and an error response indicating the product was not found.
8. IF a guest attempts to update or remove an item that does not exist in the Guest_Cart, THEN THE Cart_Service SHALL reject the request with HTTP 404 and an error response indicating the item was not found.

### Requirement 3: Guest Cart Persistence in Browser

**User Story:** As a guest shopper, I want my cart to survive page refreshes and browser sessions, so that I do not lose items I have added.

#### Acceptance Criteria

1. THE Cart_Service SHALL return the Guest_Session_Token in the response body of guest cart operations so that the client can persist it in browser local storage.
2. WHILE a Guest_Session_Token is stored in the browser, THE client SHALL include the Guest_Session_Token in subsequent guest cart requests via the X-Guest-Session request header.
3. WHEN a guest sends a request with a valid, non-expired Guest_Session_Token, THE Cart_Service SHALL return the Guest_Cart contents as they existed at the time of the last write operation (add, update, or remove item).
4. THE Cart_Service SHALL retain Guest_Cart data for a maximum of 10 days from the last write operation (add item, update item, or remove item), after which the Guest_Cart SHALL be deleted during the next cleanup cycle.
5. THE Cart_Service SHALL reset the 10-day retention period each time a write operation (add item, update item, or remove item) is performed on the Guest_Cart.
6. IF the Redis cache entry for a Guest_Cart has been evicted, THEN THE Cart_Service SHALL fall back to PostgreSQL to retrieve the Guest_Cart data and repopulate the cache.

### Requirement 4: Guest Cart Expiry and Cleanup

**User Story:** As a system operator, I want abandoned guest carts to be cleaned up automatically, so that storage resources are reclaimed.

#### Acceptance Criteria

1. WHILE a Guest_Cart has not had a write operation (add item, update item, or remove item) for 10 days, THE Cart_Service SHALL consider the Guest_Cart expired and eligible for deletion.
2. THE Cart_Service SHALL run a scheduled background cleanup process at least once every 60 minutes that identifies expired Guest_Carts and removes them in batches of up to 100 per execution cycle, deleting the Guest_Cart data from PostgreSQL and invalidating the corresponding Redis cache entry for each expired cart.
3. IF a client presents a Guest_Session_Token for an expired or deleted Guest_Cart, THEN THE Cart_Service SHALL return an empty cart response with HTTP 200 and generate a new Guest_Session_Token in the response body.
4. IF the cleanup process fails to delete a Guest_Cart from PostgreSQL, THEN THE Cart_Service SHALL skip that cart, log the failure, and continue processing remaining expired carts without stopping the batch.
5. IF the cleanup process deletes a Guest_Cart from PostgreSQL but fails to invalidate the Redis cache entry, THEN THE Cart_Service SHALL log a warning and continue, allowing the Redis entry to expire via its own TTL.

### Requirement 5: Cart Merge on Login

**User Story:** As a shopper, I want my guest cart items to transfer into my account cart when I log in, so that I do not have to re-add items.

#### Acceptance Criteria

1. WHEN a user logs in and provides a Guest_Session_Token, THE Cart_Service SHALL merge the Guest_Cart items into the user's Authenticated_Cart. IF the user does not have an existing Authenticated_Cart, THEN THE Cart_Service SHALL create a new Authenticated_Cart and add all Guest_Cart items to it.
2. WHEN Cart_Merge is triggered and a product exists only in the Guest_Cart, THE Cart_Service SHALL add that product to the Authenticated_Cart with the quantity from the Guest_Cart.
3. WHEN Cart_Merge is triggered and a product exists only in the Authenticated_Cart, THE Cart_Service SHALL retain that product and its quantity unchanged.
4. WHEN Cart_Merge is triggered and the same product exists in both the Guest_Cart and the Authenticated_Cart, THE Cart_Service SHALL use the higher quantity of the two as the merged quantity for that product, capped at a maximum of 9999 units per item.
5. WHEN Cart_Merge completes successfully, THE Cart_Service SHALL delete the Guest_Cart and invalidate the Guest_Session_Token within the same request before returning the response.
6. WHEN Cart_Merge is triggered, THE Cart_Service SHALL verify stock availability for each merged item before persisting the merged cart. IF available stock for an item is less than the merged quantity, THEN THE Cart_Service SHALL adjust the quantity down to available stock. IF available stock for an item is 0, THEN THE Cart_Service SHALL remove that item from the merged cart.
7. IF Cart_Merge encounters an inventory service failure, THEN THE Cart_Service SHALL complete the merge without stock validation and log a warning for later reconciliation.
8. IF Cart_Merge would result in the Authenticated_Cart exceeding 50 distinct items, THEN THE Cart_Service SHALL merge items up to the 50-item limit in the order they appear in the Guest_Cart and reject the remaining items, returning an indication in the response that the cart item limit was reached.

### Requirement 6: Merge Conflict Reporting

**User Story:** As a shopper, I want to be informed when my cart was adjusted during merge, so that I understand what changed.

#### Acceptance Criteria

1. WHEN Cart_Merge results in a merged quantity for a product that differs from both the original Guest_Cart quantity and the original Authenticated_Cart quantity, THE Cart_Service SHALL include an adjustment entry in the merge response for that product indicating the product identifier, the product name, the original guest quantity, the original authenticated quantity, the resulting merged quantity, and the reason for adjustment (either "conflict_resolution" or "stock_limit").
2. WHEN Cart_Merge completes and no product's merged quantity differs from both its original Guest_Cart and Authenticated_Cart quantities, THE Cart_Service SHALL return the merged cart without an adjustments collection in the response.
3. WHEN a product exists only in the Guest_Cart and its quantity is reduced due to stock limitations during Cart_Merge, THE Cart_Service SHALL report the adjustment entry with the original authenticated quantity as 0 and the reason as "stock_limit".
4. IF Cart_Merge completes without stock validation due to inventory service unavailability (per Requirement 5, criterion 7), THEN THE Cart_Service SHALL omit stock-related adjustments from the response and include a flag indicating that stock validation was skipped.

### Requirement 7: Guest Endpoint Authorization

**User Story:** As a developer, I want guest cart endpoints to be accessible without authentication, so that unauthenticated users can interact with their carts.

#### Acceptance Criteria

1. THE Cart_Service SHALL expose guest cart endpoints (GET, POST, PUT, DELETE under /api/guest-cart) that do not require JWT authentication.
2. THE Cart_Service SHALL authorize guest cart requests using the X-Guest-Session header containing a Guest_Session_Token that is a well-formed UUID and corresponds to an existing Guest_Cart.
3. IF a request to a guest cart endpoint is missing the X-Guest-Session header or contains an empty, malformed, or unrecognized Guest_Session_Token, THEN THE Cart_Service SHALL reject the request with HTTP 400 and an error message indicating an invalid or missing guest session.
4. THE Cart_Service SHALL ensure that guest cart endpoints only accept Guest_Session_Tokens for cart lookup and SHALL NOT accept user identifiers, JWT claims, or X-User-Id headers as input for resolving cart data.
5. THE Cart_Service SHALL continue to require JWT authentication on all existing authenticated cart endpoints (/api/cart and /api/checkout), returning HTTP 401 for requests without a valid JWT.
6. IF a request to a guest cart endpoint includes a JWT authorization header, THEN THE Cart_Service SHALL ignore the JWT and process the request using only the X-Guest-Session header for authorization.

### Requirement 8: Cart Merge Trigger via Login Flow

**User Story:** As a developer, I want the cart merge to be triggered seamlessly during the login flow, so that the shopper experiences a smooth transition.

#### Acceptance Criteria

1. WHEN a login request includes a Guest_Session_Token (via X-Guest-Session header), THE Auth_Service SHALL include the Guest_Session_Token as a dedicated field in the login success response body alongside the AccessToken, RefreshToken, and ExpiresIn fields.
2. WHEN the client receives a login response containing a Guest_Session_Token field, THE client SHALL call the Cart_Service POST /api/cart/merge endpoint within 5 seconds, passing the JWT access token in the Authorization header and the Guest_Session_Token in the X-Guest-Session header.
3. THE Cart_Service SHALL expose a POST /api/cart/merge endpoint that accepts an authenticated request with an X-Guest-Session header, performs the Cart_Merge operation, and returns HTTP 200 with the merged cart representation including items, quantities, unit prices, total price, and any adjustment summary as defined in Requirement 6.
4. WHEN the merge endpoint is called without an X-Guest-Session header or with a malformed Guest_Session_Token (not a valid UUID), THE Cart_Service SHALL return HTTP 400 with an error response indicating the guest session is missing or invalid.
5. IF the merge endpoint is called with a Guest_Session_Token that references an expired or non-existent Guest_Cart, THEN THE Cart_Service SHALL return HTTP 200 with the Authenticated_Cart unchanged and no adjustment summary.
6. WHEN the merge endpoint is called and the Guest_Cart is empty (zero items), THE Cart_Service SHALL return HTTP 200 with the Authenticated_Cart unchanged and no adjustment summary.
7. WHEN the merge endpoint is called and no Authenticated_Cart exists for the user, THE Cart_Service SHALL create a new Authenticated_Cart from the Guest_Cart items, applying stock validation, and return HTTP 200 with the resulting cart.

### Requirement 9: Security Constraints

**User Story:** As a system architect, I want guest cart access to be properly scoped and rate-limited, so that the system is not abused.

#### Acceptance Criteria

1. THE Cart_Service SHALL limit each Guest_Session_Token to a single Guest_Cart, and IF a request attempts to create a second Guest_Cart for an existing Guest_Session_Token, THEN THE Cart_Service SHALL return the existing Guest_Cart instead of creating a new one.
2. THE Cart_Service SHALL rate-limit guest cart creation to a maximum of 10 new sessions per IP address per sliding 60-minute window, where the IP address is determined from the X-Forwarded-For header or the connection RemoteIpAddress if X-Forwarded-For is absent.
3. IF a client exceeds the guest session creation rate limit, THEN THE Cart_Service SHALL respond with HTTP 429 and a Retry-After header whose value is the number of seconds remaining until the oldest request in the sliding window expires.
4. THE Cart_Service SHALL limit Guest_Carts to a maximum of 50 distinct items.
5. IF a guest attempts to add an item that would exceed the 50-item limit, THEN THE Cart_Service SHALL reject the request with HTTP 400 and an error message indicating the cart item limit has been reached.
6. IF a guest request includes a valid Guest_Session_Token that is associated with a different Guest_Cart than the one being accessed, THEN THE Cart_Service SHALL reject the request with HTTP 403 and an error message indicating access is denied.
