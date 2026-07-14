# K8sAuthCart — Microservices Shopping Cart Demo

A production-grade .NET 8 microservices demo featuring JWT authentication (RS256), RBAC authorization, a shopping cart with Redis cache-aside pattern, checkout with payment + Croatian fiscalization integration, and event-driven communication via RabbitMQ.

## Architecture

```
┌─────────────────┐       ┌─────────────────┐
│   AuthService   │       │   CartService   │
│   :5014         │       │   :5029         │
│                 │       │                 │
│ • Login/Register│       │ • Cart CRUD     │
│ • JWT RS256     │       │ • Checkout      │
│ • Token Refresh │       │ • Inventory     │
│ • RBAC          │       │ • Fiscalization │
└────────┬────────┘       └────────┬────────┘
         │                         │
         │    ┌─────────────┐      │
         ├───►│  PostgreSQL │◄─────┤
         │    │  :5432      │      │
         │    └─────────────┘      │
         │                         │
         │    ┌─────────────┐      │
         ├───►│    Redis    │◄─────┤
         │    │  :6379      │      │
         │    └─────────────┘      │
         │                         │
         │    ┌─────────────┐      │
         └───►│  RabbitMQ   │◄─────┘
              │  :5672/:15672│
              └─────────────┘
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for infrastructure containers)

## Quick Start

### 1. Start Infrastructure (Docker)

Run each container:

```bash
# PostgreSQL (with both databases pre-created)
docker run -d --name postgres \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 \
  postgres:16

# Redis
docker run -d --name redis \
  -p 6379:6379 \
  redis:7-alpine

# RabbitMQ (with management UI)
docker run -d --name rabbitmq \
  -p 5672:5672 \
  -p 15672:15672 \
  rabbitmq:3-management
```

### 2. Run the Services

Open two terminal windows:

```bash
# Terminal 1 — AuthService (port 5014)
dotnet run --project src/AuthService

# Terminal 2 — CartService (port 5029)
dotnet run --project src/CartService
```

Both services automatically create their databases and apply migrations on startup. No manual database setup needed.

### 3. Generate RSA Keys (optional but recommended for Auth+Cart JWT flow)

Keys are auto-generated in development, but for stable tokens across restarts:
- Navigate to the 'src' directory, then  

#### on Linux
```bash
openssl genpkey -algorithm RSA -out private.pem -pkeyopt rsa_keygen_bits:2048
openssl pkey -in private.pem -pubout -out public.pem
```

#### on Windows
 - Open gitbash where openssl is available and perform commands from above
 or
 - Install openssl and perform commands from above

This creates `private.pem` and `public.pem` in the repo root. Both services reference these in development mode.

### 4. Access Swagger UI

- **AuthService**: http://localhost:5014/swagger
- **CartService**: http://localhost:5029/swagger

## Demo Walkthrough

### Step 1: Register Users

```bash
# Register a Customer
curl -X POST http://localhost:5014/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email": "customer@test.com", "password": "Test123!", "role": "Customer"}'

# Register an Admin
curl -X POST http://localhost:5014/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email": "admin@test.com", "password": "Test123!", "role": "Admin"}'
```

### Step 2: Login & Get JWT

```bash
curl -X POST http://localhost:5014/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email": "customer@test.com", "password": "Test123!"}'
```

Response contains `accessToken` — use it for subsequent requests.

### Step 3: Add Items to Cart

```bash
# Replace <TOKEN> with the accessToken from login
curl -X POST http://localhost:5029/api/cart/items \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <TOKEN>" \
  -d '{"productId": "11111111-1111-1111-1111-111111111111", "quantity": 2}'
```

### Step 4: View Cart

```bash
curl http://localhost:5029/api/cart \
  -H "Authorization: Bearer <TOKEN>"
```

### Step 5: Checkout

```bash
curl -X POST http://localhost:5029/api/checkout \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <TOKEN>" \
  -d '{"paymentMethod": "card"}'
```

This triggers: payment authorization → capture → fiscalization → inventory decrement → cart clear → event publishing.

### Step 6: RBAC Demo — Admin-Only Inventory

```bash
# As Customer — should get 403 Forbidden:
curl http://localhost:5029/api/inventory \
  -H "Authorization: Bearer <CUSTOMER_TOKEN>"

# As Admin — should get 200 OK with product list:
curl http://localhost:5029/api/inventory \
  -H "Authorization: Bearer <ADMIN_TOKEN>"
```

### Step 7: Observe RabbitMQ Events

Open the RabbitMQ Management UI: http://localhost:15672 (guest/guest)

- **Exchanges** → `platform.events` (topic exchange, all domain events)
- **Queues** → `cart.inventory-updates` (consumes inventory.updated events)
- **Queues** → `cart.inventory-updates.dlq` (dead-letter queue for failed messages)

After a checkout, you'll see `inventory.updated` events flow through the exchange and get consumed by the CartService background worker.

## Key Features Demonstrated

| Feature | Implementation |
|---------|---------------|
| JWT RS256 Authentication | Asymmetric key signing, token refresh with rotation |
| Token Revocation | Redis-backed revocation list with graceful fallback |
| Token Family Tracking | Refresh token reuse detection, family-wide revocation |
| RBAC Authorization | Permission middleware, role→permission mapping |
| Cache-Aside Pattern | Redis L1 cache with PostgreSQL fallback |
| Cache Invalidation | Reverse index (product→users) for targeted invalidation |
| Event-Driven Messaging | RabbitMQ topic exchange, resilient publisher |
| Circuit Breakers | Polly v8 for Redis, Payment, Fiscal services |
| Dead-Letter Queue | Failed message routing after 3 retries |
| Failed Event Persistence | PostgreSQL fallback when RabbitMQ is down |
| Checkout Transactions | RepeatableRead isolation, atomic inventory updates |
| Croatian Fiscalization | ZKI generation, 48h retry window per tax law |
| Structured Logging | JSON console output with correlation IDs |
| Prometheus Metrics | /metrics endpoint for observability |

## Project Structure

```
K8sAuthCart.sln
├── src/
│   ├── AuthService/          # Authentication & Authorization
│   ├── CartService/          # Shopping Cart & Checkout
│   └── Shared/               # Interfaces, DTOs, Enums, Middleware
├── tests/
│   ├── AuthService.Tests/
│   ├── CartService.Tests/
│   └── Integration.Tests/
├── private.pem               # JWT signing key (gitignored)
└── public.pem                # JWT verification key
```

## Configuration

Both services auto-configure in Development mode. Key settings:

| Setting | AuthService | CartService |
|---------|-------------|-------------|
| Port | 5014 | 5029 |
| Database | authdb | cartdb |
| Redis | localhost:6379 | localhost:6379 |
| RabbitMQ | localhost:5672 | localhost:5672 |
| JWT Keys | private.pem + public.pem | public.pem (verify only) |

## Stopping Everything

```bash
docker stop postgres redis rabbitmq
docker rm postgres redis rabbitmq
```
