---@meta
-- EvaLuate std.* — pure C#-backed data types, always available.

---@class std
---@field color fun(...): std.color  -- color(r, g, b, a)
---@field linked_list fun(...): std.linked_list  -- linked_list()
---@field vec2 fun(...): std.vec2  -- vec2(x, y)
---@field vec3 fun(...): std.vec3  -- vec3(x, y, z)
---@field vector fun(...): std.vector  -- vector()
std = {}

---@class std.color
---@field r number
---@field g number
---@field b number
---@field a number

---@class std.linked_list
---@field push_front fun(self: std.linked_list, v: any)
---@field push_back fun(self: std.linked_list, v: any)
---@field pop_front fun(self: std.linked_list)
---@field pop_back fun(self: std.linked_list)
---@field front fun(self: std.linked_list): any
---@field back fun(self: std.linked_list): any
---@field size fun(self: std.linked_list): integer

---@class std.vec2
---@field x number
---@field y number
---@field length fun(self: std.vec2): number
---@field copy fun(self: std.vec2): std.vec2

---@class std.vec3
---@field x number
---@field y number
---@field z number
---@field length fun(self: std.vec3): number
---@field copy fun(self: std.vec3): std.vec3

---@class std.vector
---@field push_back fun(self: std.vector, v: any)
---@field pop_back fun(self: std.vector)
---@field get fun(self: std.vector, i: integer): any
---@field set fun(self: std.vector, i: integer, v: any)
---@field size fun(self: std.vector): integer
---@field clear fun(self: std.vector)

