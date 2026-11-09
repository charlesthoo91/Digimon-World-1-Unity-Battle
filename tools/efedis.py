import struct, sys
f=open(r'D:/Digimon World 1/dump/ETCHI/EFEDAT.EFE','rb').read()
t=int(sys.argv[1]); b=t*20480; m=f[b+0x34:b+20480]
I=lambda p:struct.unpack_from('<i',f,b+p)[0]
s16=lambda p:struct.unpack_from('<h',m,p)[0]
start,init,subs=I(0),I(4),I(44)
print('start',hex(start),'init',hex(init),'subs',subs,'tmd0',I(20),'tmd1',I(24),'tim0',I(28),'end',I(36))
seen=set(); todo=[init,start]; out={}
while todo:
    pc=todo.pop()
    while pc>0 and pc not in seen and pc<len(m)-2:
        seen.add(pc); v=s16(pc)&0xFFFF; op=v&0xFF; a=s16(pc+2); bb=s16(pc+4)
        sz={0:6,1:6,2:2,3:10,4:6,7:10,9:6,10:6,11:6,12:6,13:6,14:2,15:2,16:6,17:12}.get(op)
        if sz is None: out[pc]='?? op %d'%op; break
        txt={0:'reg=%d'%a,1:'reg=[%x,%d]'%(a&0xffff,bb),2:'reg=rand',3:'reg=tab',4:'op%d [%x,%d]'%(v>>12,a&0xffff,bb),7:'if%d [%x,%d] else ->%x'%(v>>12,a&0xffff,bb,s16(pc+6)&0xffff),9:'jmp %x'%(a&0xffff),10:'push %d'%a,11:'push [%x,%d]'%(a&0xffff,bb),12:'push &%x,%d'%(a&0xffff,bb),13:'call %x'%(a&0xffff),14:'SUB %d'%(v>>8),15:'ret',16:'pop->[%x,%d]'%(a&0xffff,bb),17:'spawn n=%d inst=%x'%(a,bb&0xffff)}[op]
        out[pc]=txt
        if op==7: todo.append(s16(pc+6)&0xffff)
        if op==13: todo.append(a&0xffff)
        if op==12 and bb!=0 and (a&0xffff)>=0xc00: todo.append(a&0xffff)
        if op==9: pc=a&0xffff; continue
        if op==15: break
        pc+=sz
for k in sorted(out): print('%04x %s'%(k,out[k]))
