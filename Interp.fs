(* File MicroC/Interp.c
   Interpreter for micro-C, a fraction of the C language
   sestoft@itu.dk * 2010-01-07, 2014-10-18

   A value is an integer; it may represent an integer or a pointer,
   where a pointer is just an address in the store (of a variable or
   pointer or the base address of an array).  The environment maps a
   variable to an address (location), and the store maps a location to
   an integer.  This freely permits pointer arithmetics, as in real C.
   Expressions can have side effects.  A function takes a list of
   typed arguments and may optionally return a result.

   For now, arrays can be one-dimensional only.  For simplicity, we
   represent an array as a variable which holds the address of the
   first array element.  This is consistent with the way array-type
   parameters are handled in C (and the way that array-type variables
   were handled in the B language), but not with the way array-type
   variables are handled in C.

   The store behaves as a stack, so all data are stack allocated:
   variables, function parameters and arrays.

   The return statement is not implemented (for simplicity), so all
   functions should have return type void.  But there is as yet no
   typecheck, so be careful.
 *)

module Interp

open System.Text.RegularExpressions

open Absyn
open Debug

// 带类型的数据
type memoryData =
    | INT of int
    | BOOL of bool
    | CHAR of char
    | POINTER of int
    | FLOAT of float
    | STRING of string

    member this.pointer =
        match this with
        | POINTER i -> i
        | INT i -> i
        | _ -> failwith ("not a pointer")

    member this.int =
        match this with
        | INT i -> i
        | POINTER i -> i
        | FLOAT i -> int i
        | BOOL i -> if i then 1 else 0
        | _ -> failwith ("not int")

    member this.char =
        match this with
        | CHAR i -> i
        | INT i -> char i
        | _ -> failwith ("not char")

    member this.bool =
        match this with
        | BOOL i -> i
        | _ -> failwith ("not bool")

    member this.float =
        match this with
        | FLOAT i -> i
        | INT i -> float i
        | _ -> failwith ("not float")

    member this.string =
        match this with
        | INT i -> string i
        | BOOL i -> string i
        | CHAR i -> string i
        | POINTER i -> string i
        | FLOAT i -> string i
        | STRING i -> string i

    member this.typeName =
        match this with
        | INT i -> "int"
        | BOOL i -> "bool"
        | CHAR i -> "char"
        | POINTER i -> "pointer"
        | FLOAT i -> "float"
        | STRING i -> "string"

(* Simple environment operations *)
// 多态类型 env
// 环境 env 是 元组 ("name",data) 的列表 ，名称是字符串 string 值 'data 可以是任意类型
//  名称 ---> 数据 名称与数据绑定关系的 键-值 对  key-value pairs
// [("x",9);("y",8)]: int env

type 'data env = (string * 'data) list

(* A local variable environment also knows the next unused store location *)

// ([("x",9);("y",8)],10)
// x 在位置9,y在位置8,10--->下一个空闲空间位置10
type locEnv = int env * int

(* A function environment maps a function name to parameter list and body *)
//函数参数例子:
//void func (int a , int *p)
// 参数声明列表为: [(TypI,"a");(TypP(TypI) ,"p")]
type paramdecs = (typ * string) list

(* 函数环境列表
  [("函数名", ([参数元组(类型,"名称")的列表],函数体AST)),....]

  //main (i){
  //  int r;
  //    fac (i, &r);
  //    print r;
  // }
  [ ("main",
   ([(TypI, "i")],
    Block
      [Dec (TypI,"r");
       Stmt (Expr (Call ("fac",[Access (AccVar "i"); Addr (AccVar "r")])));
       Stmt (Expr (Prim1 ("printi",Access (AccVar "r"))))]))]

函数环境 是 多态类型  'data env ---(string * 'data ) list 的一个 具体类型 ⭐⭐⭐
    类型变量 'data  具体化为  (paramdecs * stmt)
    (string * (paramdecs * stmt)) list
*)

type funEnv = (paramdecs * stmt) env

(* A global environment consists of a global variable environment
   and a global function environment
 *)

// 全局环境是 变量声明环境 和 函数声明环境的元组
// 两个列表的元组
// ([var declares...],[fun declares..])
// ( [ ("x" ,1); ("y",2) ], [("main",mainAST);("fac",facAST)] )
// mainAST,facAST 分别是main 与fac 的抽象语法树

type gloEnv = int env * funEnv

//环境查找函数
//查找名称为 x 的值的地址
let rec lookup env x =
    match env with
    | [] -> failwith (x + " not found")
    | (y, v) :: yr ->
        if x = y then
            POINTER(v)
        else
            lookup yr x

//查找名称为 x 的函数
let rec lookupFunc env x =
    match env with
    | [] -> failwith (x + " not found")
    | (y, v) :: yr -> if x = y then v else lookupFunc yr x

(* The store maps addresses (ints) to values (ints): *)

//地址是store上的的索引值
type address = int

// store 是一个 地址到值的映射，是对内存的抽象 ⭐⭐⭐
// store 是可更改的数据结构，特定位置的值可以修改，注意与环境的区别
// map{(0,3);(1,8) }
// 位置 0 保存了值 3
// 位置 1 保存了值 8
type store = Map<address, memoryData>

//空存储
let emptyStore = Map.empty<address, memoryData>

//保存value到存储store
let setSto (store: store) addr (value: memoryData) =
    match store.TryFind addr with
    | Some (data) ->
        if data.typeName = value.typeName then
            store.Add(addr, value)
        else
            failwith (
                "type error cant assign "
                + value.typeName
                + " to "
                + data.typeName
            )
    | None -> store.Add(addr, value)

//输入addr 返回存储的值value
let getSto (store: store) addr = store.Item addr

// store上从loc开始分配n个值的空间
// 用于数组分配
let rec initSto loc n store initValue =
    if n = 0 then
        store
    else // 默认值 0
        initSto (loc + 1) (n - 1) (setSto store loc initValue) initValue

(* Combined environment and store operations *)

(* Extend local variable environment so it maps x to nextloc
   (the next store location) and set store[nextloc] = v.

locEnv结构是元组 : (绑定环境env,下一个空闲地址nextloc)
store结构是Map<string,int>

扩展环境 (x nextloc) :: env ====> 新环境 (env1,nextloc+1)
变更store (nextloc) = v
 *)

// 绑定一个值 x,v 到环境
// 环境是非更改数据结构，只添加新的绑定（变量名称，存储位置），注意与store 的区别⭐⭐⭐
// 返回新环境 locEnv,更新store,
// nextloc是store上下一个空闲位置
(*
    locEnv:
    ([(n, 5); (n, 4); (g, 0)], 6)

   store:
    (0, 0)  (1, 0)(2, 0)(3, 0)(4, 1)  (5, 8)
     ^^^^    ^^^^^^^^^^^^^^^^^^^^^^    ^^^^
       g               h                n

   变量 地址 值
   n--->5--->8
   h--->4--->1
   g--->0--->0

   下一个待分配位置是 6
*)

let bindVar x v (env, nextloc) store : locEnv * store =
    let env1 = (x, nextloc) :: env
    msg $"bindVar:\n%A{env1}\n"

    //返回新环境，新的待分配位置+1，设置当前存储位置为值 v
    ((env1, nextloc + 1), setSto store nextloc v)

//将多个值 xs vs绑定到环境
//遍历 xs vs 列表,然后调用 bindVar实现单个值的绑定
let store2str store =
    String.concat "" (List.map string (Map.toList store))

let rec bindVars xs vs locEnv store : locEnv * store =
    let res =
        match (xs, vs) with
        | ([], []) -> (locEnv, store)
        | (x1 :: xr, v1 :: vr) ->
            let (locEnv1, sto1) = bindVar x1 v1 locEnv store
            bindVars xr vr locEnv1 sto1
        | _ -> failwith "parameter/argument mismatch"

    msg "\nbindVars:\n"
    msg $"\nlocEnv:\n{locEnv}"
    msg $"\nStore:\n"
    store2str store |> msg
    res
(* Allocate variable (int or pointer or array): extend environment so
   that it maps variable to next available store location, and
   initialize store location(s).
 *)
//

let rec allocate (typ: typ, name: string, value: memoryData option) (currenEnv, nextloc: int) currStore : locEnv * store =

    let defaultValue typ =
        match typ with
        | TypI -> INT(0)
        | TypC -> CHAR(' ')
        | TypB -> BOOL(false)
        | TypF -> FLOAT(0.0)
        | TypP i -> POINTER(-1)
        | TypS -> STRING("")
        | _ -> failwith ("cant init")

    let (newNextloc: int, value: memoryData, newStore: store) =
        match typ with
        //数组 调用 initSto 分配 i 个空间
        | TypA (t, Some i) -> (nextloc + i, POINTER(nextloc), initSto nextloc i currStore (defaultValue t))
        // 常规变量默认值是 0
        | _ ->
            (nextloc,
             (match value with
              | Some (x) -> x
              | None -> defaultValue typ),
             currStore)

    msg $"\nalloc:\n {((typ, name), (currenEnv, nextloc), currStore)}"
    bindVar name value (currenEnv, newNextloc) newStore

(* Build global environment of variables and functions.  For global
   variables, store locations are reserved; for global functions, just
   add to global function environment.
*)

//初始化 解释器环境和store
let initEnvAndStore (topdecs: topdec list) : locEnv * funEnv * store =

    //包括全局函数和全局变量
    msg $"\ntopdecs:\n{topdecs}\n"

    let rec addv decs locEnv funEnv store =
        match decs with
        | [] -> (locEnv, funEnv, store)

        // 全局变量声明  调用allocate 在store上给变量分配空间
        | Vardec (typ, x) :: decr ->
            let (locEnv1, sto1) = allocate (typ, x, None) locEnv store
            addv decr locEnv1 funEnv sto1

        //全局函数 将声明(f,(xs,body))添加到全局函数环境 funEnv
        | Fundec (_, f, xs, body) :: decr -> addv decr locEnv ((f, (xs, body)) :: funEnv) store

    // ([], 0) []  默认全局环境
    // locEnv ([],0) 变量环境 ，变量定义为空列表[],下一个空闲地址为0
    // ([("n", 1); ("r", 0)], 2)  表示定义了 变量 n , r 下一个可以用的变量索引是 2
    // funEnv []   函数环境，函数定义为空列表[]
    addv topdecs ([], 0) [] emptyStore

(* ------------------------------------------------------------------- *)

(* Interpreting micro-C statements *)

let rec exec stmt (locEnv: locEnv) (gloEnv: gloEnv) (store: store) : store =

    match stmt with
    | If (e, stmt1, stmt2) ->
        let (v, store1) = eval e locEnv gloEnv store

        if v.bool then
            exec stmt1 locEnv gloEnv store1 //True分支
        else
            exec stmt2 locEnv gloEnv store1 //False分支

    | While (e, body) ->

        //定义 While循环辅助函数 loop
        let rec loop store1 =
            //求值 循环条件,注意变更环境 store
            let (v, store2) = eval e locEnv gloEnv store1
            // 继续循环
            if v.bool then
                loop (exec body locEnv gloEnv store2)
            else
                store2 //退出循环返回 环境store2

        loop store


    | DoWhile (body, e) ->

        let rec loop store1 =
            let (v, store2) = eval e locEnv gloEnv store1

            if v.bool then
                loop (exec body locEnv gloEnv store2)
            else
                store2

        let do_store = exec body locEnv gloEnv store //先执行语句
        loop do_store //再进入循环

    | For (e1, e2, e3, body) ->
        let (v1, store1) = eval e1 locEnv gloEnv store //for循环首个表达式eval

        let rec loop store_ =
            let store3 = exec body locEnv gloEnv store_ //执行循环体内语句
            let (v3, store4) = eval e3 locEnv gloEnv store3 //根据for循环第三个表达式完成相应操作
            let (v4, store5) = eval e2 locEnv gloEnv store4 //判断是否依旧满足条件

            if v4.bool then
                loop (store5)
            else
                store5

        let (v2, store2) = eval e2 locEnv gloEnv store1 //判断是否满足进入for循环条件

        if v2.bool then //满足进入，反之返回当前store
            loop (store2)
        else
            store2

    | Switch (e, body) ->
        // eval switch(x)中的x
        let (v, store1) = eval e locEnv gloEnv store

        let rec fit l =
            match l with
            | [] -> store1
            | Case (e1, body1) :: tail ->
                let (v2, store2) = eval e1 locEnv gloEnv store
                //匹配成功退出
                if v2 = v then
                    exec body1 locEnv gloEnv store
                else
                    fit tail
            | _ -> failwith ("case error")

        fit body

    | Case (e, body) -> exec body locEnv gloEnv store


    | Expr e ->
        // _ 表示丢弃e的值,返回 变更后的环境store1
        let (_, store1) = eval e locEnv gloEnv store
        store1

    | Block stmts ->

        // 语句块 解释辅助函数 loop
        let rec loop ss (locEnv, store) =
            match ss with
            | [] -> store
            //语句块,解释 第1条语句s1
            // 调用loop 用变更后的环境 解释后面的语句 sr.
            | s1 :: sr -> loop sr (stmtordec s1 locEnv gloEnv store)

        loop stmts (locEnv, store)

    | Return _ -> failwith "return not implemented" // 解释器没有实现 return

and stmtordec stmtordec locEnv gloEnv store =
    match stmtordec with
    | Stmt stmt -> (locEnv, exec stmt locEnv gloEnv store)
    | Dec (typ, x) -> allocate (typ, x, None) locEnv store
    | DecAndAssign (typ, name, expr) -> allocate (typ, name, Some(fst (eval expr locEnv gloEnv store))) locEnv store

(* Evaluating micro-C expressions *)

and eval e locEnv gloEnv store : memoryData * store =
    match e with
    | Access acc ->
        let (loc, store1) = access acc locEnv gloEnv store
        (getSto store1 loc.pointer, store1)
    | Assign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        (res, setSto store2 loc.pointer res)
    | CstI i -> (INT(i), store)
    | CstB i -> (BOOL(i), store)
    | CstF i -> (FLOAT(i), store)
    | CstS i -> (STRING(i), store)
    | CstC i -> (CHAR(i),store)
    | Addr acc -> access acc locEnv gloEnv store
    | Prim1 (ope, e1) ->
        let (i1, store1) = eval e1 locEnv gloEnv store

        let res =
            match ope with
            | "!" -> BOOL(not i1.bool)
            | "printi" ->
                (printf "%d " i1.int
                 i1)
            | "printc" ->
                (printf "%c" i1.char
                 i1)
            | _ -> failwith ("unknown primitive " + ope)

        (res, store1)
    | Printf (ope , e1) ->
        let (i1,store1) = eval e1 locEnv gloEnv store

        let res = 
            match ope with
            | "%c"   -> (printf "%c " i1.char; i1)
            | "%d"   -> (printf "%d " i1.int;i1)  
            | "%s"   -> (printf "%s " i1.string;i1) 
            | "%f"   -> (printf "%f " i1.float;i1)
            | "%x"   -> (printf "%x"  i1.int;i1)
            | "%o"   -> (printf "%o"  i1.int;i1) 
            | _      -> (printf "%s " i1.string;i1) 

        (res, store1)

    | Prim2 (ope, e1, e2) ->

        let (i1, store1) = eval e1 locEnv gloEnv store
        let (i2, store2) = eval e2 locEnv gloEnv store1

        let res =
            match ope with
            | "*" ->
                match (i1) with
                | INT i -> 
                    if(i2.typeName = i1.typeName) then INT(i1.int * i2.int)
                    else FLOAT(i1.float * i2.float)
                | FLOAT i -> 
                    if(i2.typeName = i1.typeName) then FLOAT(i1.float * i2.float)
                    else FLOAT(i1.float * i2.float)
                | _ -> failwith ("cant TIMES")
            | "+" ->
                match i1 with
                | INT i -> 
                    if(i2.typeName = i1.typeName) then INT(i1.int + i2.int)
                    else FLOAT(i1.float + i2.float)
                | FLOAT i -> 
                    if(i2.typeName = i1.typeName) then FLOAT(i1.float + i2.float)
                    else FLOAT(i1.float + i2.float)
                | _ -> failwith ("cant PLUS")
            | "-" ->
                match i1 with
                | INT i -> 
                    if(i2.typeName = i1.typeName) then INT(i1.int - i2.int)
                    else FLOAT(i1.float - i2.float)
                | FLOAT i -> 
                    if(i2.typeName = i1.typeName) then FLOAT(i1.float - i2.float)
                    else FLOAT(i1.float - i2.float)
                | _ -> failwith ("cant MINUS")
            | "/" ->
                match i1 with
                | INT i -> 
                    if(i2.typeName = i1.typeName) then INT(i1.int / i2.int)
                    else FLOAT(i1.float / i2.float)
                | FLOAT i -> 
                    if(i2.typeName = i1.typeName) then FLOAT(i1.float / i2.float)
                    else FLOAT(i1.float / i2.float)
                | _ -> failwith ("cant DIV")
            | "%" ->
                match i2 with
                | INT i -> 
                    if(i2.typeName = i1.typeName) then INT(i1.int % i2.int)
                    else failwith ("cant MOD")
                | _ -> failwith ("cant MOD")

            | "==" ->
                if i1 = i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | "!=" ->
                if i1 <> i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | "<" ->
                if i1 < i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | "<=" ->
                if i1 <= i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | ">=" ->
                if i1 >= i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | ">" ->
                if i1 > i2 then
                    BOOL(true)
                else
                    BOOL(false)
            | _ -> failwith ("unknown primitive " + ope)

        (res, store2)

    | Prim3 (cond, e1, e2) ->
        let (v1, store1) = eval cond locEnv gloEnv store

        if v1.bool then
            let (v2, store2) = eval e1 locEnv gloEnv store1
            (v2, store2)
        else
            let (v3, store3) = eval e2 locEnv gloEnv store1
            (v3, store3)
            
    | Andalso (e1, e2) ->
        let (i1, store1) as res = eval e1 locEnv gloEnv store

        if i1.bool then
            eval e2 locEnv gloEnv store1
        else
            res
    | PlusAssign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        let tmp = getSto store1 loc.pointer

        let var =
            match tmp with
            | INT i -> 
                if(tmp.typeName = res.typeName) then INT(tmp.int + res.int)
                else FLOAT(tmp.float + res.float)
            | FLOAT i -> FLOAT(tmp.float + res.float)
            | _ -> failwith ("please input int or float")

        (var, setSto store2 loc.pointer var)

    | MinusAssign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        let tmp = getSto store1 loc.pointer

        let var =
            match tmp with
            | INT i -> 
                if(tmp.typeName = res.typeName) then INT(tmp.int - res.int)
                else FLOAT(tmp.float - res.float)
            | FLOAT i -> FLOAT(tmp.float - res.float)
            | _ -> failwith ("please input int or float")

        (var, setSto store2 loc.pointer var)

    | TimesAssign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        let tmp = getSto store1 loc.pointer

        let var =
            match tmp with
            | INT i -> 
                if(tmp.typeName = res.typeName) then INT(tmp.int * res.int)
                else FLOAT(tmp.float * res.float)
            | FLOAT i -> FLOAT(tmp.float * res.float)
            | _ -> failwith ("please input int or float")

        (var, setSto store2 loc.pointer var)

    | DivAssign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        let tmp = getSto store1 loc.pointer

        let var =
            match tmp with
            | INT i -> 
                if(tmp.typeName = res.typeName) then INT(tmp.int / res.int)
                else FLOAT(tmp.float / res.float)
            | FLOAT i -> FLOAT(tmp.float / res.float)
            | _ -> failwith ("please input int or float")

        (var, setSto store2 loc.pointer var)

    | ModAssign (acc, e) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let (res, store2) = eval e locEnv gloEnv store1
        let tmp = getSto store1 loc.pointer

        let var =
            match tmp with
            | INT i -> 
                if(tmp.typeName = res.typeName) then INT(tmp.int % res.int)
                else failwith ("please input int or float")
            | _ -> failwith ("please input int or float")

        (var, setSto store2 loc.pointer var)

    | PrePlus (ope, acc) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let tmp = getSto store1 loc.pointer
        let var =
            match tmp with
            | INT i -> INT(tmp.int + 1)
            | FLOAT i -> FLOAT(tmp.float+1.0)
            | _ -> failwith ("please input int or float")
        (var, setSto store loc.pointer var)

    | RearPlus (acc, ope) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let tmp = getSto store1 loc.pointer
        let var =
            match tmp with
            | INT i -> INT(tmp.int + 1)
            | FLOAT i -> FLOAT(tmp.float+1.0)
            | _ -> failwith ("please input int or float")
        (var, setSto store loc.pointer var)

    | PreMinus (ope, acc) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let tmp = getSto store1 loc.pointer
        let var =
            match tmp with
            | INT i -> INT(tmp.int - 1)
            | FLOAT i -> FLOAT(tmp.float-1.0)
            | _ -> failwith ("please input int or float")
        (var, setSto store loc.pointer var)

    | RearMinus (acc, ope) ->
        let (loc, store1) = access acc locEnv gloEnv store
        let tmp = getSto store1 loc.pointer
        let var =
            match tmp with
            | INT i -> INT(tmp.int - 1)
            | FLOAT i -> FLOAT(tmp.float-1.0)
            | _ -> failwith ("please input int or float")
        (var, setSto store loc.pointer var)

    | Orelse (e1, e2) ->
        let (i1, store1) as res = eval e1 locEnv gloEnv store

        if i1.bool then
            res
        else
            eval e2 locEnv gloEnv store1
    | Call (f, es) -> callfun f es locEnv gloEnv store

and access acc locEnv gloEnv store : memoryData * store =
    match acc with
    | AccVar x -> (lookup (fst locEnv) x, store)
    | AccDeref e -> eval e locEnv gloEnv store
    | AccIndex (acc, idx) ->
        let (a, store1) = access acc locEnv gloEnv store
        let aval = getSto store1 a.pointer
        let (i, store2) = eval idx locEnv gloEnv store1
        (POINTER(aval.pointer + i.int), store2)

and evals es locEnv gloEnv store : memoryData list * store =
    match es with
    | [] -> ([], store)
    | e1 :: er ->
        let (v1, store1) = eval e1 locEnv gloEnv store
        let (vr, storer) = evals er locEnv gloEnv store1
        (v1 :: vr, storer)

and callfun f es locEnv gloEnv store : memoryData * store =

    msg
    <| sprintf "callfun: %A\n" (f, locEnv, gloEnv, store)

    let (_, nextloc) = locEnv
    let (varEnv, funEnv) = gloEnv
    let (paramdecs, fBody) = lookupFunc funEnv f
    let (vs, store1) = evals es locEnv gloEnv store

    let (fBodyEnv, store2) =
        bindVars (List.map snd paramdecs) vs (varEnv, nextloc) store1

    let store3 = exec fBody fBodyEnv gloEnv store2
    (INT(-111), store3)

(* Interpret a complete micro-C program by initializing the store
   and global environments, then invoking its `main' function.
 *)

// run 返回的结果是 代表内存更改的 store 类型
// vs 参数列表 [8,2,...]
// 可以为空 []
let run (Prog topdecs) vs =
    //
    let ((varEnv, nextloc), funEnv, store0) = initEnvAndStore topdecs

    // mainParams 是 main 的参数列表
    //
    let (mainParams, mainBody) = lookupFunc funEnv "main"

    let (mainBodyEnv, store1) =
        bindVars (List.map snd mainParams) vs (varEnv, nextloc) store0


    msg
    <|

    //以ex9.c为例子
    // main的 AST
    sprintf "\nmainBody:\n %A\n" mainBody
    +

    //局部环境
    // 如
    // i 存储在store位置0,store中下个空闲位置是1
    //([("i", 0)], 1)

    sprintf "\nmainBodyEnv:\n %A\n" mainBodyEnv
    +

    //全局环境 (变量,函数定义)
    // fac 的AST
    // main的 AST
    sprintf $"\n varEnv:\n {varEnv} \nfunEnv:\n{funEnv}\n"
    +

    //当前存储
    // store 中 0 号 位置存储值为8
    // map [(0, 8)]
    sprintf "\nstore1:\n %A\n" store1

    let endstore =
        exec mainBody mainBodyEnv (varEnv, funEnv) store1

    msg $"\nvarEnv:\n{varEnv}"
    msg $"\nStore:\n"
    msg <| store2str endstore

    endstore

(* Example programs are found in the files ex1.c, ex2.c, etc *)
