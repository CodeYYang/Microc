(* File MicroC/Absyn.fs
   Abstract syntax of micro-C, an imperative language.
   sestoft@itu.dk 2009-09-25

   Must precede Interp.fs, Comp.fs and Contcomp.fs in Solution Explorer
 *)

module Absyn

// 基本类型
// 注意，数组、指针是递归类型
// 这里没有函数类型，注意与上次课的 MicroML 对比
type typ =
    | TypI // Type int
    | TypC // Type char
    | TypB // Type boolean
    | TypA of typ * int option // Array type
    | TypP of typ // Pointer type
    | TypF // Type float
    | TypS // Type string

and expr =
    | Access of access // x    or  *p    or  a[e]      //访问左值（右值）
    | Assign of access * expr // x=e  or  *p=e  or  a[e]=e
    | Addr of access // &x   or  &*p   or  &a[e]
    | CstI of int // Constant
    | CstB of bool // Constant Bool
    | CstF of float
    | CstS of string
    | CstC of char
    | Prim1 of string * expr // Unary primitive operator
    | Prim2 of string * expr * expr // Binary primitive operator
    | Print of string * expr     // Print operator
    | Printf of string * expr     // Print operator
    | Prim3 of expr * expr * expr //三目运算符
    | PlusAssign of access * expr // x += a
    | MinusAssign of access * expr // x -= a
    | TimesAssign of access * expr // x *=  a
    | DivAssign of access * expr // x /= a
    | PrePlus of string * access // ++x
    | RearPlus of access * string // x++
    | PreMinus of string * access // --x
    | RearMinus of access * string // x--
    | ModAssign of access * expr // x %= a
    | Andalso of expr * expr // Sequential and
    | Orelse of expr * expr // Sequential or
    | Call of string * expr list // Function call f(...)

and access =
    | AccVar of string // Variable access        x
    | AccDeref of expr // Pointer dereferencing  *p
    | AccIndex of access * expr // Array indexing         a[e]

and stmt =
    | If of expr * stmt * stmt // Conditional
    | While of expr * stmt // While loop
    | DoWhile of stmt * expr // DoWhile loop
    | For of expr * expr * expr * stmt // For loop
    | Switch of expr * stmt list // Switch def
    | Case of expr * stmt // Case def
    | Expr of expr // Expression statement   e;
    | Return of expr option // Return from method
    | Block of stmtordec list // Block: grouping and scope
// 语句块内部，可以是变量声明 或语句的列表

and stmtordec =
    | Dec of typ * string // Local variable declaration
    | Stmt of stmt // A statement
    | DecAndAssign of typ * string * expr // Assign variable when declaration

// 顶级声明 可以是函数声明或变量声明
and topdec =
    | Fundec of typ option * string * (typ * string) list * stmt
    | Vardec of typ * string

// 程序是顶级声明的列表
and program = Prog of topdec list
